// QuotaGauge — Claude Code と Codex の利用枠を通知領域から確認する常駐ツール
//
//   左クリック … 使用率のパネルを開く
//   右クリック … 更新 / ログ / 起動時に開始 / 終了
//
// 取得元
//   Claude Code … 既定は `claude` を起動して control_request {subtype:"get_usage"} を投げる。
//                 Claude Code 自身が OAuth を更新して取ってくるので、こちらは認証情報にも
//                 ネットワークにも触れない。モデルは呼ばれないので課金もされない。
//                 右クリックから、直接叩く経路／ステータスライン経由にも切り替えられる
//   Codex       … codex app-server の JSON-RPC `account/rateLimits/read` を呼ぶ
//
// 取得した値はローカルに表示するだけで、第三者のサーバーへは何も送らない。
//
// ビルド: build.ps1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("QuotaGauge")]
[assembly: System.Reflection.AssemblyProduct("QuotaGauge")]
[assembly: System.Reflection.AssemblyDescription("Shows Claude Code and Codex quota in the notification area")]
[assembly: System.Reflection.AssemblyCompany("kimura")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("2.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.2.0.0")]

namespace QuotaGauge {

// 表示言語。日本語と英語を呼び出し側で隣に並べて持つ。
// 別の場所に対訳表を作ると片方だけ古くなるので、必ず同じ行に置く
static class S {
  static bool? ja;

  public static bool Ja {
    get {
      if (ja.HasValue) return ja.Value;
      string cfg = Config.Language;                       // auto / ja / en
      if (cfg == "ja")      ja = true;
      else if (cfg == "en") ja = false;
      else ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
      return ja.Value;
    }
  }

  public static string T(string japanese, string english) { return Ja ? japanese : english; }
}

// ------------------------------------------------------------------ データ
class Limit {
  public string Label = "";
  public int Percent;
  public string Severity = "normal";
  public DateTime? ResetsAt;

  public bool IsCritical { get { return Severity == "critical" || Percent >= 90; } }

  // 値が古いと（statusline のキャッシュなど）リセット時刻が過去を指す。
  // その場合は残り時間を出さない。おかしな値を自信ありげに見せない方がいい
  public bool ResetIsUsable {
    get { return ResetsAt.HasValue && (ResetsAt.Value - DateTime.Now).TotalSeconds > 0; }
  }

  public string Remaining {
    get {
      if (!ResetIsUsable) return "";
      TimeSpan t = ResetsAt.Value - DateTime.Now;
      if (t.TotalHours >= 1) return string.Format(S.T("あと {0}時間{1}分", "resets in {0}h {1}m"), (int)t.TotalHours, t.Minutes);
      return string.Format(S.T("あと {0}分", "resets in {0}m"), Math.Max(1, (int)t.TotalMinutes));
    }
  }
}

class Provider {
  public string Key = "";          // "claude" / "codex"
  public string Name = "";
  public string Note;              // プラン名など
  public DateTime? DataTime;       // 値そのものの鮮度（取得時刻ではない）
  public List<Limit> Limits = new List<Limit>();
  public string Error;

  public string Heading {
    get {
      string s = string.IsNullOrEmpty(Note) ? Name : Name + " · " + Note;
      // 鮮度は常に出す。値がいつのものか分からないと、他の表示との数%のズレを誤解する
      if (DataTime.HasValue) s += S.T("（", " (") + Ago(DataTime.Value) + S.T("の値）", ")");
      return s;
    }
  }

  // 値が古いほど、他所の表示とズレる。何分前かを見せておく
  public bool IsStale {
    get { return DataTime.HasValue && (DateTime.Now - DataTime.Value).TotalMinutes >= 5; }
  }

  static string Ago(DateTime t) {
    int sec = (int)(DateTime.Now - t).TotalSeconds;
    if (sec < 45) return S.T("たった今", "just now");
    int min = (int)Math.Round(sec / 60.0);
    if (min < 60) return min + S.T("分前", "m ago");
    return (min / 60) + S.T("時間前", "h ago");
  }
}

class Snapshot {
  public List<Provider> Providers = new List<Provider>();
  public DateTime FetchedAt;

  // source が "claude"/"codex" ならそのプロバイダだけ、それ以外なら全部を見る
  static bool Match(Provider p, string source) {
    return source != "claude" && source != "codex" ? true : p.Key == source;
  }

  public int WorstOf(string source) {
    int max = 0;
    foreach (var p in Providers) {
      if (!Match(p, source)) continue;
      foreach (var l in p.Limits) if (l.Percent > max) max = l.Percent;
    }
    return max;
  }

  public bool CriticalIn(string source) {
    foreach (var p in Providers) {
      if (!Match(p, source)) continue;
      foreach (var l in p.Limits) if (l.IsCritical) return true;
    }
    return false;
  }

  public bool HasDataIn(string source) {
    foreach (var p in Providers) if (Match(p, source) && p.Limits.Count > 0) return true;
    return false;
  }

  public int RowCount {
    get {
      int n = 0;
      foreach (var p in Providers) n += Math.Max(1, p.Limits.Count);
      return n;
    }
  }
}

// 必要なフィールドしか読まないので、JSONライブラリは足さずに素直に拾う
static class Json {
  public static string Str(string s, string key) {
    var m = Regex.Match(s, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
    return m.Success ? m.Groups[1].Value : null;
  }

  public static double? Num(string s, string key) {
    var m = Regex.Match(s, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
    if (!m.Success) return null;
    return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
  }

  // "key": { ... } のオブジェクトを丸ごと取り出す
  public static string Object(string json, string key) {
    if (json == null) return null;
    int at = json.IndexOf("\"" + key + "\"");
    if (at < 0) return null;
    int colon = json.IndexOf(':', at);
    if (colon < 0) return null;
    int open = json.IndexOf('{', colon);
    if (open < 0) return null;
    // 途中に別のキーが挟まっていたら、それは目的のオブジェクトではない
    if (json.IndexOf('"', colon + 1) >= 0 && json.IndexOf('"', colon + 1) < open) return null;

    int depth = 0;
    for (int i = open; i < json.Length; i++) {
      if (json[i] == '{') depth++;
      else if (json[i] == '}') { depth--; if (depth == 0) return json.Substring(open, i - open + 1); }
    }
    return null;
  }

  public static DateTime? Iso(string s, string key) {
    string v = Str(s, key);
    DateTime dt;
    if (!string.IsNullOrEmpty(v) &&
        DateTime.TryParse(v, CultureInfo.InvariantCulture,
                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
      return dt.ToLocalTime();
    return null;
  }

  // オブジェクト配列を要素ごとの文字列に切り出す
  public static List<string> Objects(string json, string key) {
    var list = new List<string>();
    int at = json.IndexOf("\"" + key + "\"");
    if (at < 0) return list;
    int open = json.IndexOf('[', at);
    if (open < 0) return list;

    int depth = 1, objStart = -1;
    for (int i = open + 1; i < json.Length; i++) {
      char c = json[i];
      if (c == ']' && depth == 1) break;
      if (c == '{') { if (depth == 1) objStart = i; depth++; }
      else if (c == '}') {
        depth--;
        if (depth == 1 && objStart >= 0) { list.Add(json.Substring(objStart, i - objStart + 1)); objStart = -1; }
      }
    }
    return list;
  }

  public static DateTime FromUnix(double seconds) {
    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
  }

  public static string WindowLabel(double minutes) {
    if (minutes >= 60 * 24 * 6.5) return S.T("週次", "Weekly");
    if (minutes >= 60 * 24) return ((int)Math.Round(minutes / (60 * 24))) + S.T("日枠", "-day");
    if (minutes >= 60) return ((int)Math.Round(minutes / 60)) + S.T("時間枠", "-hour");
    return ((int)Math.Round(minutes)) + S.T("分枠", "-min");
  }
}

// ------------------------------------------------------------------ Claude Code
// 取得元は3つ。どれを使うかは Config.ClaudeSource。
//   cli（既定）  … Claude Code 自身に聞く。認証情報にもネットワークにも触れず、値は本体と同じ
//   endpoint     … 同じ問い合わせ先を直接叩く。速いが、認証情報を読みネットワークへ出る
//   statusline   … statusline スクリプトが書いたキャッシュを読む。5時間枠と週次だけ
static class ClaudeApi {
  public static Provider Fetch() {
    switch (Config.ClaudeSource) {
      case "statusline": return FromStatusLine();
      case "endpoint":   return FromEndpoint();
      default:           return FromCli();
    }
  }

  // --- 既定：Claude Code 自身に聞く ---------------------------------------------
  // `claude` を stream-json モードで起動して control_request {subtype:"get_usage"} を1行流す。
  // Claude Code が OAuth を更新したうえで自分の利用枠を取ってくるので、
  // こちらは認証情報にもネットワークにも触らない。モデルは呼ばれないので課金もされない。
  // Codex を app-server 越しに聞いているのと同じ形。
  static Provider FromCli() {
    var p = new Provider { Key = "claude", Name = "Claude Code" };
    try {
      string res = CallClaude();
      if (res == null) { p.Error = S.T("claude から応答がありません（PATH とログイン状態を確認）", "No response from claude (check your PATH and that you are signed in)"); return p; }
      if (Json.Str(res, "subtype") == "error") {
        p.Error = Json.Str(res, "error") ?? S.T("利用枠を取得できませんでした", "Could not read the usage data");
        return p;
      }

      p.Note = Json.Str(res, "subscription_type");
      ParseLimits(p, res);
      p.DataTime = DateTime.Now;
      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報が空でした", "The usage data was empty");
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }

  static string CallClaude() {
    var psi = new ProcessStartInfo("claude",
      "-p --input-format stream-json --output-format stream-json --verbose " +
      // MCP の読み込みが起動時間の半分を占めるので落とす。hook も要らない
      "--strict-mcp-config --mcp-config \"{\\\"mcpServers\\\":{}}\" --settings \"{\\\"hooks\\\":{}}\"");
    psi.WorkingDirectory = Paths.WorkDir;
    psi.UseShellExecute = false;
    psi.RedirectStandardInput = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.CreateNoWindow = true;
    psi.StandardOutputEncoding = Encoding.UTF8;
    // telegram プラグインが入っている環境で、常駐中のポーラーを止めさせない
    psi.EnvironmentVariables["TELEGRAM_STATE_DIR"] =
      Path.Combine(Path.GetTempPath(), "quotagauge-no-telegram");

    Process proc = null;
    try {
      proc = Process.Start(psi);
      proc.StandardInput.WriteLine(
        "{\"type\":\"control_request\",\"request_id\":\"1\",\"request\":{\"subtype\":\"get_usage\"}}");
      proc.StandardInput.Flush();
      proc.StandardInput.Close();

      string found = null;
      var reader = new Thread(delegate () {
        try {
          string line;
          while ((line = proc.StandardOutput.ReadLine()) != null)
            if (line.Contains("\"control_response\"")) { found = line; break; }
        } catch { }
      });
      reader.IsBackground = true;
      reader.Start();
      // 実測でおよそ8秒。遅い環境でも取りこぼさないよう余裕を持って待つ
      if (!reader.Join(60000)) { try { proc.Kill(); } catch { } }
      return found;
    } finally {
      if (proc != null) {
        try { if (!proc.WaitForExit(3000)) proc.Kill(); } catch { }
        try { proc.Dispose(); } catch { }
      }
    }
  }

  // limits[] は cli と endpoint で同じ構造。両方から使う
  static void ParseLimits(Provider p, string body) {
    foreach (var obj in Json.Objects(body, "limits")) {
      var l = new Limit();
      l.Percent  = (int)Math.Round(Json.Num(obj, "percent") ?? 0);
      l.Severity = Json.Str(obj, "severity") ?? "normal";
      l.ResetsAt = Json.Iso(obj, "resets_at");

      string kind = Json.Str(obj, "kind") ?? "";
      var scope = Regex.Match(obj, "\"scope\"\\s*:\\s*\\{.*?\"display_name\"\\s*:\\s*\"([^\"]+)\"",
                              RegexOptions.Singleline);
      if (scope.Success)             l.Label = S.T("週次（", "Weekly (") + scope.Groups[1].Value + S.T("）", ")");
      else if (kind == "session")    l.Label = S.T("5時間枠", "5-hour");
      else if (kind == "weekly_all") l.Label = S.T("週次（全体）", "Weekly (all)");
      else                           l.Label = kind;

      p.Limits.Add(l);
    }
  }

  // --- もう一方：同じ問い合わせ先を自分で直接叩く -------------------------------
  // 速いが、認証情報を読みネットワークへ出る。トークンを更新できないので期限切れで 401 になる。
  // 公開されたインターフェースでもない（README の注意書きを参照）
  const string Url = "https://api.anthropic.com/api/oauth/usage";
  const string Beta = "oauth-2025-04-20";

  // トークンが切れている（401）状態で3分ごとに叩き続けると、そのまま 429 を踏み続ける。
  // 失敗が続くあいだは間隔を伸ばす。成功したら元に戻す
  static int failCount;
  static DateTime retryAfter = DateTime.MinValue;
  static string lastError = "";

  // 「今すぐ更新」を押されたときは、待機中でもすぐ試す
  public static void ResetBackoff() {
    failCount = 0;
    retryAfter = DateTime.MinValue;
  }

  // 3分 → 6 → 12 → 24 → 48 → 60分（上限）
  static void Backoff(string err) {
    failCount++;
    int mins = (int)Math.Min(60, 3 * Math.Pow(2, Math.Min(failCount - 1, 5)));
    retryAfter = DateTime.Now.AddMinutes(mins);
    lastError = err;
  }

  static string WaitLabel(TimeSpan t) {
    int m = (int)Math.Ceiling(t.TotalMinutes);
    return m >= 60 ? (m / 60) + S.T("時間", "h") : m + S.T("分", "m");
  }

  static string CredentialsPath {
    get {
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".claude", ".credentials.json");
    }
  }

  static Provider FromEndpoint() {
    var p = new Provider { Key = "claude", Name = "Claude Code" };

    if (DateTime.Now < retryAfter) {
      p.Error = lastError + string.Format(S.T("（{0}後に再試行）", " (retrying in {0})"), WaitLabel(retryAfter - DateTime.Now));
      return p;
    }

    try {
      string cred = File.ReadAllText(CredentialsPath, Encoding.UTF8);
      var tok = Regex.Match(cred, "\"accessToken\"\\s*:\\s*\"([^\"]+)\"");
      if (!tok.Success) throw new Exception(S.T("Claude Code にログインしていません", "You are not signed in to Claude Code"));

      ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
      var req = (HttpWebRequest)WebRequest.Create(Url);
      req.Method = "GET";
      req.Timeout = 15000;
      req.UserAgent = "QuotaGauge";
      req.Headers["Authorization"] = "Bearer " + tok.Groups[1].Value;
      req.Headers["anthropic-beta"] = Beta;

      string body;
      using (var res = (HttpWebResponse)req.GetResponse())
      using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
        body = sr.ReadToEnd();

      ParseLimits(p, body);
      p.DataTime = DateTime.Now;
      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報が空でした", "The usage data was empty");
      ResetBackoff();
    } catch (WebException wex) {
      var r = wex.Response as HttpWebResponse;
      int code = r != null ? (int)r.StatusCode : 0;
      // 何が起きているか分からないと直しようがないので、コードごとに次の一手を書く
      if (code == 401)      p.Error = S.T("ログインし直してください（トークンの期限切れ・HTTP 401）", "Sign in to Claude Code again (token expired, HTTP 401)");
      else if (code == 429) p.Error = S.T("問い合わせが多すぎます（HTTP 429）", "Too many requests (HTTP 429)");
      else if (code != 0)   p.Error = S.T("取得できません (HTTP ", "Could not fetch (HTTP ") + code + ")";
      else                  p.Error = S.T("取得できません: ", "Could not fetch: ") + wex.Message;
      Backoff(p.Error);
    } catch (Exception ex) {
      p.Error = ex.Message;
      Backoff(p.Error);
    }
    return p;
  }

  // --- もう一方：ステータスライン経由 ------------------------------------------
  // 渡ってくるのは 5時間枠と週次だけ（モデル別の枠は含まれない）。
  // ステータスラインが呼ばれた時点の値なので、放置すると古くなる
  public static string CachePath {
    get {
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".claude", "quota-cache.json");
    }
  }

  static Provider FromStatusLine() {
    var p = new Provider { Key = "claude", Name = "Claude Code" };
    try {
      if (!File.Exists(CachePath)) {
        p.Error = S.T("ステータスラインの設定が必要です（README参照）", "The status line needs to be set up (see the README)");
        return p;
      }
      string json = File.ReadAllText(CachePath, Encoding.UTF8);
      AddCached(p, json, "five_hour", S.T("5時間枠", "5-hour"));
      AddCached(p, json, "seven_day", S.T("週次", "Weekly"));

      double? upd = Json.Num(json, "updated_at");
      if (upd.HasValue) p.DataTime = Json.FromUnix(upd.Value);

      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報がありません", "No usage data");
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }

  static void AddCached(Provider p, string json, string key, string label) {
    string obj = Json.Object(json, key);
    if (obj == null) return;
    double? pct = Json.Num(obj, "used_percentage");
    if (!pct.HasValue) return;

    var l = new Limit { Label = label, Percent = (int)Math.Round(pct.Value) };
    l.Severity = l.Percent >= 90 ? "critical" : "normal";
    double? reset = Json.Num(obj, "resets_at");
    if (reset.HasValue) l.ResetsAt = Json.FromUnix(reset.Value);
    p.Limits.Add(l);
  }
}

// ------------------------------------------------------------------ Codex
// codex app-server の JSON-RPC を使う。メソッドは codex app-server generate-json-schema で
// 公開されているスキーマに定義されているもの。
static class CodexApi {
  public static Provider Fetch() {
    var p = new Provider { Key = "codex", Name = "Codex" };
    try {
      string res = Call();
      if (res == null) { p.Error = S.T("codex app-server から応答がありません", "No response from codex app-server"); return p; }

      string rl = Json.Object(res, "rateLimits");
      if (rl == null) { p.Error = S.T("利用枠の情報がありません", "No usage data"); return p; }

      p.Note = Json.Str(rl, "planType");
      Add(p, Json.Object(rl, "primary"));
      Add(p, Json.Object(rl, "secondary"));
      p.DataTime = DateTime.Now;

      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報が空でした", "The usage data was empty");
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }

  static void Add(Provider p, string obj) {
    if (string.IsNullOrEmpty(obj)) return;
    double? used = Json.Num(obj, "usedPercent");
    if (!used.HasValue) return;

    var l = new Limit { Percent = (int)Math.Round(used.Value) };
    l.Severity = l.Percent >= 90 ? "critical" : "normal";

    double? win = Json.Num(obj, "windowDurationMins");
    l.Label = win.HasValue ? Json.WindowLabel(win.Value) : S.T("利用枠", "Usage");

    double? reset = Json.Num(obj, "resetsAt");
    if (reset.HasValue) l.ResetsAt = Json.FromUnix(reset.Value);

    p.Limits.Add(l);
  }

  static string Call() {
    var psi = new ProcessStartInfo("cmd.exe", "/c codex app-server");
    psi.WorkingDirectory = Paths.WorkDir;
    psi.UseShellExecute = false;
    psi.RedirectStandardInput = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.CreateNoWindow = true;
    psi.StandardOutputEncoding = Encoding.UTF8;
    // Codex CLI に telegram プラグインが入っていると、起動時に既存のポーラーを止めてしまう。
    // 状態ディレクトリを別の場所へ逃がして、常駐中のものに触らせない
    psi.EnvironmentVariables["TELEGRAM_STATE_DIR"] =
      Path.Combine(Path.GetTempPath(), "quotagauge-no-telegram");

    Process proc = null;
    try {
      proc = Process.Start(psi);
      proc.StandardInput.WriteLine(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":" +
        "{\"name\":\"QuotaGauge\",\"title\":\"QuotaGauge\",\"version\":\"2.2.0\"}}}");
      proc.StandardInput.Flush();
      proc.StandardInput.WriteLine(
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
      proc.StandardInput.Flush();

      // 通知が混ざって流れてくるので、目的の id の行が来るまで読み進める。
      // 応答が返らないまま ReadLine で止まると、3分ごとに codex のプロセスが積み上がる。
      // 読みは別スレッドに任せて、待つのは15秒まで
      string found = null;
      var reader = new Thread(delegate () {
        try {
          for (int i = 0; i < 200; i++) {
            string line = proc.StandardOutput.ReadLine();
            if (line == null) break;
            if (line.Contains("\"id\":2")) { found = line; break; }
          }
        } catch { }
      });
      reader.IsBackground = true;
      reader.Start();
      if (!reader.Join(15000)) { try { proc.Kill(); } catch { } }
      return found;
    } finally {
      if (proc != null) {
        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.WaitForExit(3000)) proc.Kill(); } catch { }
        try { proc.Dispose(); } catch { }
      }
    }
  }
}

static class Usage {
  public static Snapshot FetchAll() {
    var s = new Snapshot { FetchedAt = DateTime.Now };
    s.Providers.Add(ClaudeApi.Fetch());
    s.Providers.Add(CodexApi.Fetch());
    return s;
  }
}

// ------------------------------------------------------------------ 配色
static class Palette {
  public static readonly Color Bg      = Color.FromArgb(255, 255, 255);
  public static readonly Color Border  = Color.FromArgb(226, 232, 240);
  public static readonly Color Text    = Color.FromArgb(15, 23, 42);
  public static readonly Color SubText = Color.FromArgb(71, 85, 105);
  public static readonly Color Heading = Color.FromArgb(100, 116, 139);
  public static readonly Color Track   = Color.FromArgb(241, 245, 249);
  public static readonly Color BarOk   = Color.FromArgb(100, 116, 139);
  public static readonly Color BarWarn = Color.FromArgb(180, 120, 40);
  public static readonly Color BarCrit = Color.FromArgb(159, 42, 42);

  // トレイアイコン用。タスクバーが明色でも暗色でも輪郭が残るよう彩度を上げてある
  public static readonly Color IconTrack = Color.FromArgb(125, 135, 150);
  public static readonly Color IconOk    = Color.FromArgb(148, 163, 184);
  public static readonly Color IconWarn  = Color.FromArgb(217, 119, 6);
  public static readonly Color IconCrit  = Color.FromArgb(220, 62, 62);

  public static Color BarFor(Limit l) {
    if (l.IsCritical) return BarCrit;
    if (l.Percent >= 70) return BarWarn;
    return BarOk;
  }
}

// ------------------------------------------------------------------ パネル
class QuotaPanel : Form {
  Snapshot snap;
  readonly Font fTitle   = new Font("Yu Gothic UI", 10F, FontStyle.Bold);
  readonly Font fHeading = new Font("Yu Gothic UI", 8.5F, FontStyle.Bold);
  readonly Font fLabel   = new Font("Yu Gothic UI", 9.5F, FontStyle.Regular);
  readonly Font fPct     = new Font("Yu Gothic UI", 13F, FontStyle.Bold);
  readonly Font fSub     = new Font("Yu Gothic UI", 8.5F, FontStyle.Regular);
  readonly Font fBtn     = new Font("Yu Gothic UI", 9F, FontStyle.Regular);
  Button refreshBtn;

  const int RowH = 60;
  const int HeadH = 26;

  // フォーカスが外れて隠れた時刻。トレイアイコンでの開閉判定に使う（下の JustHidden）
  DateTime hiddenAt = DateTime.MinValue;

  // トレイアイコンを押すと、クリックが届く前に Deactivate が飛んでパネルが隠れる。
  // そのため OnClick の時点では「閉じている」ように見えて、開き直してしまう。
  // 隠れた直後かどうかを見れば、その1回が「閉じる操作」だったと分かる
  public bool JustHidden {
    get { return (DateTime.Now - hiddenAt).TotalMilliseconds < 300; }
  }

  public event EventHandler RefreshRequested;

  public QuotaPanel() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Palette.Bg;
    Width = 360;
    DoubleBuffered = true;

    refreshBtn = new Button();
    refreshBtn.Text = S.T("更新", "Refresh");
    refreshBtn.FlatStyle = FlatStyle.Flat;
    refreshBtn.FlatAppearance.BorderColor = Palette.Border;
    refreshBtn.FlatAppearance.BorderSize = 1;
    refreshBtn.BackColor = Palette.Bg;
    refreshBtn.ForeColor = Palette.SubText;
    refreshBtn.Font = fBtn;
    refreshBtn.Size = new Size(64, 26);
    refreshBtn.Cursor = Cursors.Hand;
    refreshBtn.Click += delegate {
      if (RefreshRequested != null) RefreshRequested(this, EventArgs.Empty);
    };
    Controls.Add(refreshBtn);

    Deactivate += delegate { hiddenAt = DateTime.Now; Hide(); };
  }

  // 取得し直した結果をパネルへ反映する。
  // これを呼ばないと、開いたときの値を描き続けて「更新しても何も変わらない」ように見える
  public void UpdateSnapshot(Snapshot s) {
    snap = s;
    if (Visible) Invalidate();
  }

  // 押しても何も起きないように見えると、更新できたのか分からない
  public void SetBusy(bool busy) {
    refreshBtn.Enabled = !busy;
    refreshBtn.Text = busy ? S.T("更新中", "Refreshing") : S.T("更新", "Refresh");
    refreshBtn.Refresh();
  }

  public void ShowAt(Snapshot s, Point anchor) {
    snap = s;
    hiddenAt = DateTime.MinValue;

    int rows = (s != null && s.RowCount > 0) ? s.RowCount : 1;
    int heads = (s != null) ? s.Providers.Count : 1;
    Height = 46 + heads * HeadH + rows * RowH + 44;

    var wa = Screen.FromPoint(anchor).WorkingArea;
    int x = Math.Min(Math.Max(wa.Left + 8, anchor.X - Width / 2), wa.Right - Width - 8);
    Location = new Point(x, wa.Bottom - Height - 8);

    refreshBtn.Location = new Point(Width - refreshBtn.Width - 16, Height - refreshBtn.Height - 12);

    Invalidate();
    Show();
    Activate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

    using (var p = new Pen(Palette.Border))
      g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

    using (var b = new SolidBrush(Palette.Text))
      g.DrawString(S.T("利用枠", "Usage"), fTitle, b, 16, 14);

    int y = 44;

    if (snap == null) {
      using (var b = new SolidBrush(Palette.SubText))
        g.DrawString(S.T("読み込み中…", "Loading…"), fLabel, b, 16, y + 8);
    } else {
      foreach (var pv in snap.Providers) {
        // 値が古いときは見出しの色を変えて、他所の表示とのズレに気づけるようにする
        using (var b = new SolidBrush(pv.IsStale ? Palette.BarWarn : Palette.Heading))
          g.DrawString(pv.Heading, fHeading, b, 16, y);
        y += HeadH;

        if (pv.Limits.Count == 0) {
          using (var b = new SolidBrush(Palette.SubText))
            g.DrawString(pv.Error ?? S.T("情報なし", "No data"), fSub, b, 16, y);
          y += RowH;
          continue;
        }
        foreach (var l in pv.Limits) { DrawRow(g, l, y); y += RowH; }
      }
    }

    using (var b = new SolidBrush(Palette.SubText))
      g.DrawString(snap == null ? "" : S.T("最終取得 ", "Updated ") + snap.FetchedAt.ToString("HH:mm:ss"),
                   fSub, b, 16, Height - 30);
  }

  void DrawRow(Graphics g, Limit l, int y) {
    using (var b = new SolidBrush(Palette.Text))
      g.DrawString(l.Label, fLabel, b, 16, y);

    string pct = l.Percent + "%";
    var size = g.MeasureString(pct, fPct);
    using (var b = new SolidBrush(Palette.BarFor(l)))
      g.DrawString(pct, fPct, b, Width - 16 - size.Width, y - 4);

    int barY = y + 23, barW = Width - 32, barH = 6;
    using (var b = new SolidBrush(Palette.Track))
      g.FillRectangle(b, 16, barY, barW, barH);
    int fill = (int)Math.Round(barW * Math.Min(100, Math.Max(0, l.Percent)) / 100.0);
    if (fill > 0)
      using (var b = new SolidBrush(Palette.BarFor(l)))
        g.FillRectangle(b, 16, barY, fill, barH);

    // 使った量（右上の数字）と、まだ使える量を両方見せる
    string sub = string.Format(S.T("残り {0}%", "{0}% left"), Math.Max(0, 100 - l.Percent));
    if (l.ResetIsUsable)
      sub += S.T(" ・ ", " · ") + l.Remaining + S.T("（", " (") + l.ResetsAt.Value.ToString("M/d HH:mm") + S.T("）", ")");
    using (var b = new SolidBrush(Palette.SubText))
      g.DrawString(sub, fSub, b, 16, barY + 11);
  }
}

// ------------------------------------------------------------------ 常駐
class TrayApp : ApplicationContext {
  [DllImport("user32.dll", CharSet = CharSet.Auto)]
  static extern bool DestroyIcon(IntPtr handle);

  NotifyIcon ni;
  ContextMenuStrip menu;
  System.Windows.Forms.Timer timer;
  QuotaPanel panel;
  Snapshot snap;
  Icon currentIcon;

  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string RunValue = "QuotaGauge";

  public TrayApp() {
    menu = new ContextMenuStrip();
    menu.Opening += BuildMenu;

    ni = new NotifyIcon();
    // 取得が終わるまでの一瞬と、描画に失敗したときの拠り所。exe に埋めたリングを使う
    try { ni.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
    catch { ni.Icon = SystemIcons.Application; }
    ni.Text = S.T("利用枠ゲージ", "QuotaGauge");
    ni.ContextMenuStrip = menu;
    ni.Visible = true;
    ni.MouseClick += OnClick;

    panel = new QuotaPanel();
    panel.RefreshRequested += delegate { Reload(true); };
    // 別スレッドの取得結果をUIへ戻すため、表示前にウィンドウハンドルを作っておく
    { IntPtr dummy = panel.Handle; }

    timer = new System.Windows.Forms.Timer();
    timer.Interval = 3 * 60 * 1000;
    timer.Tick += delegate { Reload(); };
    timer.Start();

    UpdateIcon();
    Reload();
  }

  int reloading;                                          // 0=待機 1=取得中
  readonly Dictionary<string, string> lastLogged = new Dictionary<string, string>();

  void Reload() { Reload(false); }

  // manual=true は「今すぐ更新」。待機中のバックオフを無視してすぐ試す
  void Reload(bool manual) {
    if (manual) ClaudeApi.ResetBackoff();

    // 前の取得がまだ終わっていないなら重ねない。
    // codex app-server が応答しないとき、3分ごとにスレッドとプロセスが積み上がってしまう
    if (Interlocked.CompareExchange(ref reloading, 1, 0) != 0) return;

    try { panel.SetBusy(true); } catch { }

    var t = new Thread(delegate () {
      Snapshot s;
      try { s = Usage.FetchAll(); }
      catch (Exception ex) {
        s = new Snapshot { FetchedAt = DateTime.Now };
        Log.Write("Fetch failed: " + ex.Message);
      }

      MethodInvoker apply = delegate {
        snap = s;
        try { panel.UpdateSnapshot(s); panel.SetBusy(false); } catch { }
        UpdateIcon();
        foreach (var p in s.Providers) LogIfNew(p);
      };
      try {
        if (panel.IsHandleCreated) panel.BeginInvoke(apply);
        else apply();
      } catch (Exception ex) { Log.Write("Reload: " + ex.Message); }
      finally { Interlocked.Exchange(ref reloading, 0); }
    });
    t.IsBackground = true;
    t.Start();
  }

  // 同じエラーが3分ごとに並ぶとログが読めなくなる。内容が変わったときだけ書く
  void LogIfNew(Provider p) {
    string cur = p.Error ?? "";
    string prev;
    if (lastLogged.TryGetValue(p.Key, out prev) && prev == cur) return;
    lastLogged[p.Key] = cur;
    if (cur.Length > 0) Log.Write(p.Name + ": " + cur);
  }

  // 使用率をリング（円弧）で描く。32pxに数字を入れても読めないので、量は角度で示す
  void UpdateIcon() {
    try {
      string src = Config.IconSource;
      bool hasData = snap != null && snap.HasDataIn(src);
      int pct = hasData ? snap.WorstOf(src) : 0;
      Color color = !hasData ? Palette.IconTrack
                  : (snap.CriticalIn(src) ? Palette.IconCrit
                  : (pct >= 70 ? Palette.IconWarn : Palette.IconOk));

      using (var bmp = new Bitmap(32, 32))
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new Rectangle(4, 4, 23, 23);
        using (var track = new Pen(Palette.IconTrack, 5f))
          g.DrawArc(track, rect, 0, 360);

        if (!hasData) {
          using (var b = new SolidBrush(Palette.IconTrack))
            g.FillEllipse(b, 13, 13, 6, 6);
        } else if (pct > 0) {
          using (var pen = new Pen(color, 5f)) {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, rect, -90, 360f * Math.Min(100, pct) / 100f);
          }
        }

        IntPtr h = bmp.GetHicon();
        var old = currentIcon;
        currentIcon = Icon.FromHandle(h);
        ni.Icon = currentIcon;
        if (old != null) { IntPtr oh = old.Handle; old.Dispose(); DestroyIcon(oh); }
      }

      var sb = new StringBuilder();
      if (snap != null)
        foreach (var p in snap.Providers) {
          if (p.Limits.Count == 0) continue;
          int worst = 0;
          foreach (var l in p.Limits) if (l.Percent > worst) worst = l.Percent;
          if (sb.Length > 0) sb.Append("\r\n");
          sb.Append(p.Name + " " + worst + "%");
        }
      string tip = sb.Length == 0 ? S.T("利用枠ゲージ", "QuotaGauge") : sb.ToString();
      ni.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
    } catch (Exception ex) { Log.Write("UpdateIcon: " + ex.Message); }
  }

  void OnClick(object sender, MouseEventArgs e) {
    if (e.Button != MouseButtons.Left) return;
    if (panel.Visible || panel.JustHidden) { panel.Hide(); return; }
    panel.ShowAt(snap, Cursor.Position);
  }

  void BuildMenu(object sender, System.ComponentModel.CancelEventArgs e) {
    menu.Items.Clear();

    var open = new ToolStripMenuItem(S.T("利用枠を見る", "Show usage"));
    open.Click += delegate { panel.ShowAt(snap, Cursor.Position); };
    menu.Items.Add(open);

    var reload = new ToolStripMenuItem(S.T("今すぐ更新", "Refresh now"));
    reload.Click += delegate { Reload(true); };
    menu.Items.Add(reload);

    var log = new ToolStripMenuItem(S.T("ログを見る", "View log"));
    log.Enabled = File.Exists(Log.Path);
    log.Click += delegate {
      // パスにスペースが入ると引数が割れるので必ず括る
      try { Process.Start("notepad.exe", "\"" + Log.Path + "\""); } catch { }
    };
    menu.Items.Add(log);

    menu.Items.Add(new ToolStripSeparator());

    // 主に使うツールは人によって違うので、アイコンが映す対象を選べるようにする
    var iconSrc = new ToolStripMenuItem(S.T("アイコンに出す対象", "Icon follows"));
    AddIconSource(iconSrc, "both",   S.T("厳しい方", "Whichever is tighter"));
    AddIconSource(iconSrc, "claude", "Claude Code");
    AddIconSource(iconSrc, "codex",  "Codex");
    menu.Items.Add(iconSrc);

    var claudeSrc = new ToolStripMenuItem(S.T("Claude の取得元", "Claude data source"));
    AddClaudeSource(claudeSrc, "cli",        S.T("Claude Code に聞く（既定）", "Ask Claude Code (default)"));
    AddClaudeSource(claudeSrc, "endpoint",   S.T("同じ問い合わせ先を直接（速い）", "Call the endpoint directly (faster)"));
    AddClaudeSource(claudeSrc, "statusline", S.T("ステータスライン経由（古い値になる）", "Via the status line (goes stale)"));
    menu.Items.Add(claudeSrc);

    var startup = new ToolStripMenuItem(S.T("Windows起動時に開始", "Start with Windows"));
    startup.Checked = RunAtStartup;
    startup.Click += delegate { RunAtStartup = !RunAtStartup; };
    menu.Items.Add(startup);

    menu.Items.Add(new ToolStripSeparator());

    var quit = new ToolStripMenuItem(S.T("終了", "Quit"));
    quit.Click += delegate {
      ni.Visible = false;
      ni.Dispose();
      ExitThread();
    };
    menu.Items.Add(quit);
  }

  void AddIconSource(ToolStripMenuItem root, string key, string label) {
    var item = new ToolStripMenuItem(label);
    item.Checked = (Config.IconSource == key);
    item.Click += delegate { Config.IconSource = key; UpdateIcon(); };
    root.DropDownItems.Add(item);
  }

  void AddClaudeSource(ToolStripMenuItem root, string key, string label) {
    var item = new ToolStripMenuItem(label);
    item.Checked = (Config.ClaudeSource == key);
    item.Click += delegate { Config.ClaudeSource = key; Reload(true); };
    root.DropDownItems.Add(item);
  }

  static bool RunAtStartup {
    get {
      using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
        return k != null && k.GetValue(RunValue) != null;
    }
    set {
      using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
        if (k == null) return;
        if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
        else k.DeleteValue(RunValue, false);
      }
    }
  }
}

// 設定とログの置き場所。持ち運べるように、まずは exe と同じフォルダを使う。
// Program Files のように書き込めない場所へ置かれたときだけ %LOCALAPPDATA% へ逃がす
static class Paths {
  static string dir;
  static string work;

  // 子プロセス（claude / codex）を起こす場所。
  // 指定しないと exe を置いたフォルダがカレントになり、そこに置かれた同名の実行ファイルを
  // 先に拾ってしまう（cmd も CreateProcess もカレントを検索順に含む）。専用の空き場所へ固定する
  public static string WorkDir {
    get {
      if (work != null) return work;
      work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotagauge-work");
      try { Directory.CreateDirectory(work); } catch { work = System.IO.Path.GetTempPath(); }
      return work;
    }
  }

  public static string DataDir {
    get {
      if (dir != null) return dir;

      string beside = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
      if (IsWritable(beside)) return dir = beside;

      string fallback = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaGauge");
      try { Directory.CreateDirectory(fallback); } catch { }
      return dir = fallback;
    }
  }

  static bool IsWritable(string d) {
    try {
      string probe = System.IO.Path.Combine(d, ".quotagauge-write-test");
      File.WriteAllText(probe, "");
      File.Delete(probe);
      return true;
    } catch { return false; }
  }
}

static class Config {
  public static string Path {
    get { return System.IO.Path.Combine(Paths.DataDir, "config.json"); }
  }

  static string Read(string key, string fallback) {
    try {
      if (!File.Exists(Path)) return fallback;
      var m = Regex.Match(File.ReadAllText(Path, Encoding.UTF8),
                          "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
      return (m.Success && m.Groups[1].Value.Length > 0) ? m.Groups[1].Value : fallback;
    } catch { return fallback; }
  }

  static void Write(string iconSource, string claudeSource) { Write(iconSource, claudeSource, Language); }

  static void Write(string iconSource, string claudeSource, string language) {
    try {
      File.WriteAllText(Path,
        "{\r\n" +
        "  \"_comment\": \"iconSource: which provider the tray icon reflects (both|claude|codex). " +
        "claudeSource: where Claude numbers come from (cli|endpoint|statusline). language: auto|ja|en (auto follows Windows).\",\r\n" +
        "  \"iconSource\": \"" + iconSource + "\",\r\n" +
        "  \"claudeSource\": \"" + claudeSource + "\",\r\n" +
        "  \"language\": \"" + language + "\"\r\n" +
        "}\r\n", new UTF8Encoding(false));
    } catch { }
  }

  // アイコンがどのプロバイダを映すか。"both"（既定）/ "claude" / "codex"
  // 主に使うツールが人によって違うので、選べるようにしてある
  public static string IconSource {
    get { return Read("iconSource", "both"); }
    set { Write(value, ClaudeSource); }
  }

  // Claude の数値をどこから取るか。
  //   "cli"（既定）  … Claude Code 自身に聞く。認証情報にもネットワークにも触れず、値は本体と同じ
  //   "endpoint"     … 同じ問い合わせ先を直接叩く。速いが認証情報を読む
  //   "statusline"   … statusline のキャッシュを読む。5時間枠と週次だけ
  public static string ClaudeSource {
    get { return Read("claudeSource", "cli"); }
    set { Write(IconSource, value); }
  }

  // 表示言語。"auto"（既定）は Windows の表示言語に従う。"ja" / "en" で固定できる
  public static string Language {
    get { return Read("language", "auto"); }
  }
}

static class Log {
  public static string Path {
    get { return System.IO.Path.Combine(Paths.DataDir, "quotagauge.log"); }
  }

  public static void Write(string msg) {
    try {
      File.AppendAllText(Path,
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
        new UTF8Encoding(false));
      var lines = File.ReadAllLines(Path, Encoding.UTF8);
      if (lines.Length > 200) {
        var keep = new string[200];
        Array.Copy(lines, lines.Length - 200, keep, 0, 200);
        File.WriteAllLines(Path, keep, new UTF8Encoding(false));
      }
    } catch { }
  }
}

static class Program {
  [STAThread]
  static void Main() {
    bool created;
    using (var mutex = new Mutex(true, "Local\\QuotaGaugeTray", out created)) {
      if (!created) return;

      // 例外はダイアログではなくログへ。常駐ツールが前面に出て作業を止めないようにする
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += delegate (object s, ThreadExceptionEventArgs ev) {
        Log.Write("Exception: " + ev.Exception);
      };
      AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs ev) {
        Log.Write("Unhandled exception: " + ev.ExceptionObject);
      };

      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      Application.Run(new TrayApp());
      GC.KeepAlive(mutex);
    }
  }
}

}
