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
//   agy         … （任意・既定OFF）`agy --print "/quota"` の print モード。turn は走らず枠も減らない
//   grok        … （任意・既定OFF）`grok agent stdio` の ACP 拡張 `_x.ai/billing` を呼ぶ
//
// 4つとも「CLI 自身に聞く」形で揃えてある。認証情報にもネットワークにも触れない。
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
[assembly: System.Reflection.AssemblyVersion("2.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.3.0.0")]

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

  // リングの下に置く短い形。今日なら時刻、先なら日付
  public string ResetLabel {
    get {
      if (!ResetIsUsable) return "";
      var r = ResetsAt.Value;
      return r.Date == DateTime.Now.Date ? r.ToString("HH:mm") : r.ToString("M/d");
    }
  }

  public string Remaining {
    get {
      if (!ResetIsUsable) return "";
      TimeSpan t = ResetsAt.Value - DateTime.Now;
      // 週次の枠は「167時間」より「6日23時間」の方が読める
      if (t.TotalHours >= 24) return string.Format(S.T("あと {0}日{1}時間", "resets in {0}d {1}h"), t.Days, t.Hours);
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

  public string Freshness { get { return DataTime.HasValue ? Ago(DataTime.Value) : ""; } }

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

  // source が "both"（か空）なら全部、それ以外はその Key のプロバイダだけを見る
  static bool Match(Provider p, string source) {
    return string.IsNullOrEmpty(source) || source == "both" ? true : p.Key == source;
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

// ------------------------------------------------------------------ 子プロセス共通
// stdin に何行か流し、stdout を match が真になる行まで読む。時間切れなら殺す。
// 通知が混ざって流れる JSON-RPC 系はどれもこの形なので、agy / grok はこれを使う
static class Subprocess {
  public static string ReadUntil(string command, string args, string[] stdinLines,
                                 Predicate<string> match, int timeoutMs) {
    var psi = new ProcessStartInfo(command, args);
    psi.WorkingDirectory = Paths.WorkDir;
    psi.UseShellExecute = false;
    psi.RedirectStandardInput = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.CreateNoWindow = true;
    psi.StandardOutputEncoding = Encoding.UTF8;
    psi.EnvironmentVariables["TELEGRAM_STATE_DIR"] =
      Path.Combine(Path.GetTempPath(), "quotagauge-no-telegram");

    Process proc = null;
    try {
      proc = Process.Start(psi);
      foreach (var l in stdinLines) { proc.StandardInput.WriteLine(l); proc.StandardInput.Flush(); }
      if (stdinLines.Length == 0) proc.StandardInput.Close();

      string found = null;
      var reader = new Thread(delegate () {
        try {
          for (int i = 0; i < 500; i++) {
            string line = proc.StandardOutput.ReadLine();
            if (line == null) break;
            if (match(line)) { found = line; break; }
          }
        } catch { }
      });
      reader.IsBackground = true;
      reader.Start();
      if (!reader.Join(timeoutMs)) { try { proc.Kill(); } catch { } }
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

// ------------------------------------------------------------------ agy（Antigravity CLI）
// print モードは読み取り専用のスラッシュコマンドに「turn を起こさず」答える（agy 1.1.11 以降）。
// `/quota` は週次のグループ（Gemini 系／Claude・GPT 系）ごとに remaining_fraction と reset_time を返す。
// ⚠ bash から試すときは `MSYS_NO_PATHCONV=1` が要る（`/quota` が Windows パスに化ける）。ここは cmd 経由なので無縁
static class AgyApi {
  public static Provider Fetch() {
    var p = new Provider { Key = "agy", Name = "Antigravity" };
    try {
      string res = Subprocess.ReadUntil("cmd.exe", "/c agy --output-format json --print /quota",
                                        new string[0], delegate (string l) { return l.Contains("\"groups\""); }, 40000);
      if (res == null) { p.Error = S.T("agy から応答がありません（PATH とログイン状態を確認）", "No response from agy (check your PATH and that you are signed in)"); return p; }
      // turn が走ったなら /quota として扱われていない
      if ((Json.Num(res, "num_turns") ?? 0) > 0) { p.Error = S.T("agy が /quota を実行しませんでした（版が古い？）", "agy did not answer /quota (old version?)"); return p; }

      foreach (var grp in Json.Objects(res, "groups")) {
        string name = Json.Str(grp, "name") ?? "";
        string shortName = name.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0 ? "Gemini"
                         : name.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 ? "Claude/GPT" : name;
        foreach (var b in Json.Objects(grp, "buckets")) {
          double? rem = Json.Num(b, "remaining_fraction");
          if (!rem.HasValue) continue;
          var l = new Limit { Percent = (int)Math.Round((1 - rem.Value) * 100) };
          l.Severity = l.Percent >= 90 ? "critical" : "normal";
          string win = Json.Str(b, "window") ?? "";
          // リングの下は幅が狭い。週次しか無いので「週次」は書かず、モデル群の名前だけにする
          l.Label = win == "weekly" ? shortName : shortName + " (" + win + ")";
          l.ResetsAt = Json.Iso(b, "reset_time");
          p.Limits.Add(l);
        }
      }
      p.DataTime = DateTime.Now;
      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報が空でした", "The usage data was empty");
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }
}

// ------------------------------------------------------------------ grok（Grok Build）
// `grok agent stdio` は ACP（JSON-RPC）で話す。拡張メソッドは `_x.ai/…`（先頭アンダースコア）。
// `_x.ai/billing` が creditUsagePercent と週の期間を返す。クレジット制なので「使った%」1本＋期限。
// ⚠ initialize 直後の通知に MCP 設定の環境変数（トークン類）がそのまま流れてくる。stdout を絶対にログへ落とさない
static class GrokApi {
  public static Provider Fetch() {
    var p = new Provider { Key = "grok", Name = "Grok Build" };
    try {
      string res = Subprocess.ReadUntil("cmd.exe", "/c grok agent stdio", new string[] {
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1," +
        "\"clientCapabilities\":{\"fs\":{\"readTextFile\":false,\"writeTextFile\":false},\"terminal\":false}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"_x.ai/billing\",\"params\":{}}"
      }, delegate (string l) { return l.Contains("\"id\":2"); }, 40000);
      if (res == null) { p.Error = S.T("grok から応答がありません（PATH とログイン状態を確認）", "No response from grok (check your PATH and that you are signed in)"); return p; }
      if (res.Contains("\"error\"")) { p.Error = Json.Str(res, "message") ?? S.T("利用枠を取得できませんでした", "Could not read the usage data"); return p; }

      string cfg = Json.Object(res, "config");
      if (cfg == null) { p.Error = S.T("利用枠の情報がありません", "No usage data"); return p; }
      p.Note = Json.Str(res, "subscription_tier");

      double? used = Json.Num(cfg, "creditUsagePercent");
      if (used.HasValue) {
        // creditUsagePercent は % として読んでいる（1.0 = 1%）。割合なら 100 倍が要るが、その場合 model が動いた事実と合わない
        var l = new Limit { Percent = (int)Math.Round(used.Value) };
        l.Severity = l.Percent >= 90 ? "critical" : "normal";
        string period = Json.Object(cfg, "currentPeriod");
        string type = period != null ? (Json.Str(period, "type") ?? "") : "";
        l.Label = type.Contains("WEEKLY") ? S.T("週次クレジット", "Weekly credits") : S.T("クレジット", "Credits");
        if (period != null) l.ResetsAt = Json.Iso(period, "end");
        p.Limits.Add(l);
      }
      // 従量（on-demand）を使う設定なら、その消化も1本出す
      string cap = Json.Object(cfg, "onDemandCap"), od = Json.Object(cfg, "onDemandUsed");
      double? capV = cap != null ? Json.Num(cap, "val") : null, odV = od != null ? Json.Num(od, "val") : null;
      if (capV.HasValue && capV.Value > 0 && odV.HasValue) {
        var l = new Limit { Label = S.T("従量分", "On-demand"), Percent = (int)Math.Round(odV.Value * 100 / capV.Value) };
        l.Severity = l.Percent >= 90 ? "critical" : "normal";
        p.Limits.Add(l);
      }
      p.DataTime = DateTime.Now;
      if (p.Limits.Count == 0) p.Error = S.T("利用枠の情報が空でした", "The usage data was empty");
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }
}

static class Usage {
  // 4本を直列に待つと 30秒近くかかるので、並列に取って一番遅いもの（Claude の約8秒）に揃える
  public static Snapshot FetchAll() {
    var s = new Snapshot { FetchedAt = DateTime.Now };
    var jobs = new List<Func<Provider>> { ClaudeApi.Fetch, CodexApi.Fetch };
    if (Config.AgyEnabled)  jobs.Add(AgyApi.Fetch);
    if (Config.GrokEnabled) jobs.Add(GrokApi.Fetch);

    var results = new Provider[jobs.Count];
    var threads = new List<Thread>();
    for (int i = 0; i < jobs.Count; i++) {
      int idx = i;
      var t = new Thread(delegate () {
        try { results[idx] = jobs[idx](); }
        catch (Exception ex) { results[idx] = new Provider { Key = "?", Name = "?", Error = ex.Message }; }
      });
      t.IsBackground = true;
      t.Start();
      threads.Add(t);
    }
    foreach (var t in threads) t.Join(90000);
    foreach (var r in results) if (r != null) s.Providers.Add(r);
    return s;
  }
}

// ------------------------------------------------------------------ 配色
static class Palette {
  // 毎日開く常駐UIなので独自の配色に振らず、Google Power Tools の sidepanel.css と同じトークンを使う
  public static readonly Color Bg           = Color.FromArgb(255, 255, 255);   // --surface
  public static readonly Color Border       = Color.FromArgb(235, 235, 235);   // --border (黒 8%)
  public static readonly Color BorderStrong = Color.FromArgb(217, 217, 217);   // --border-strong (黒 15%)
  public static readonly Color Text         = Color.FromArgb(24, 24, 27);      // --text
  public static readonly Color SubText      = Color.FromArgb(95, 99, 104);     // --text-muted
  public static readonly Color Heading      = Color.FromArgb(128, 134, 139);   // --text-faint
  public static readonly Color Track        = Color.FromArgb(238, 238, 240);   // --surface-2
  public static readonly Color BarOk        = Color.FromArgb(95, 99, 104);     // --text-muted
  public static readonly Color BarWarn      = Color.FromArgb(180, 83, 9);      // --warning
  public static readonly Color BarCrit      = Color.FromArgb(236, 64, 47);     // --danger

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
  readonly Font fHeading = new Font("Yu Gothic UI", 9.5F, FontStyle.Bold);
  readonly Font fLabel   = new Font("Yu Gothic UI", 9.5F, FontStyle.Regular);
  readonly Font fPct     = new Font("Segoe UI", 9.5F, FontStyle.Bold);       // リングの中の数字
  readonly Font fSub     = new Font("Yu Gothic UI", 8.5F, FontStyle.Regular);
  readonly Font fTiny    = new Font("Segoe UI", 8F, FontStyle.Regular);
  readonly Font fBtn     = new Font("Yu Gothic UI", 9F, FontStyle.Regular);
  Button refreshBtn;

  // 枠ごとにトレイと同じリング（円弧）を並べる。文章で3回書いていた同じ情報を、絵1つと数字1つにする。
  // プロバイダは見出し1行＋リングの段（3つ横並び）＋区切り線
  const int HeadH = 34;        // 見出し行（上の余白込み）
  const int TileH = 104;       // リング1段
  const int TilesPerRow = 3;
  const int Side = 16;         // 左右の余白
  const int Ring = 52;         // リングの直径
  const int RingStroke = 6;
  const int FootH = 44;
  const int ErrH = 40;         // 取れなかったときの1行

  static int BlockHeight(Provider p) {
    int rows = p.Limits.Count == 0 ? 0 : (p.Limits.Count + TilesPerRow - 1) / TilesPerRow;
    return HeadH + (rows == 0 ? ErrH : rows * TileH) + 1;
  }

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
    refreshBtn.FlatAppearance.BorderColor = Palette.BorderStrong;
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

    int body = 0;
    if (s != null && s.Providers.Count > 0) {
      foreach (var p in s.Providers) body += BlockHeight(p);
    } else {
      body = ErrH + 8;                                      // 「読み込み中…」の1行ぶん
    }
    Height = 4 + body + FootH;

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

    using (var p = new Pen(Palette.BorderStrong))
      g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

    int y = 4;

    if (snap == null || snap.Providers.Count == 0) {
      using (var b = new SolidBrush(Palette.SubText))
        g.DrawString(snap == null ? S.T("読み込み中…", "Loading…") : S.T("情報なし", "No data"), fLabel, b, Side, y + 14);
      y += ErrH + 8;
    } else {
      bool first = true;
      foreach (var pv in snap.Providers) {
        if (!first)
          using (var p = new Pen(Palette.Border)) g.DrawLine(p, Side, y, Width - Side, y);
        first = false;

        // 見出し＝名前（濃い・太字）と、プラン・鮮度（薄い・右寄せ）
        int hy = y + 12;
        using (var b = new SolidBrush(Palette.Text))
          g.DrawString(pv.Name, fHeading, b, Side - 2, hy);
        string right = pv.Note ?? "";
        if (pv.DataTime.HasValue) right += (right.Length > 0 ? S.T(" ・ ", " · ") : "") + pv.Freshness;
        if (right.Length > 0) {
          var sz = g.MeasureString(right, fSub);
          using (var b = new SolidBrush(pv.IsStale ? Palette.BarWarn : Palette.Heading))
            g.DrawString(right, fSub, b, Width - Side - sz.Width + 2, hy + 2);
        }
        y += HeadH;

        if (pv.Limits.Count == 0) {
          using (var b = new SolidBrush(Palette.SubText))
            g.DrawString(pv.Error ?? S.T("情報なし", "No data"), fSub, b,
                         new RectangleF(Side, y, Width - Side * 2, ErrH));
          y += ErrH;
        } else {
          int tileW = (Width - Side * 2) / TilesPerRow;
          for (int i = 0; i < pv.Limits.Count; i++) {
            int col = i % TilesPerRow, row = i / TilesPerRow;
            DrawTile(g, pv.Limits[i], Side + col * tileW, y + row * TileH, tileW);
          }
          y += ((pv.Limits.Count + TilesPerRow - 1) / TilesPerRow) * TileH;
        }
        y += 1;
      }
    }

    // 下段＝取得時刻と「更新」
    using (var p = new Pen(Palette.Border)) g.DrawLine(p, 0, Height - FootH, Width, Height - FootH);
    using (var b = new SolidBrush(Palette.Heading))
      g.DrawString(snap == null ? "" : snap.FetchedAt.ToString("HH:mm") + S.T(" 取得", " fetched"),
                   fSub, b, Side, Height - FootH + 15);
  }

  // リング1つ＝円弧・中の%・下にラベルとリセット
  void DrawTile(Graphics g, Limit l, int x, int y, int w) {
    int cx = x + w / 2;
    var rect = new Rectangle(cx - Ring / 2 + RingStroke / 2, y + 10 + RingStroke / 2, Ring - RingStroke, Ring - RingStroke);
    using (var track = new Pen(Palette.Track, RingStroke))
      g.DrawEllipse(track, rect);
    int pct = Math.Min(100, Math.Max(0, l.Percent));
    if (pct > 0)
      using (var pen = new Pen(Palette.BarFor(l), RingStroke)) {
        if (pct < 100) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; }
        g.DrawArc(pen, rect, -90, 360f * pct / 100f);
      }

    var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    using (var b = new SolidBrush(l.IsCritical ? Palette.BarCrit : (l.Percent >= 70 ? Palette.BarWarn : Palette.Text)))
      g.DrawString(l.Percent + "%", fPct, b, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), center);

    var top = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    using (var b = new SolidBrush(Palette.SubText))
      g.DrawString(l.Label, fSub, b, new RectangleF(x, y + 10 + Ring + 6, w, 16), top);
    if (l.ResetIsUsable)
      using (var b = new SolidBrush(Palette.Heading))
        g.DrawString(l.ResetLabel, fTiny, b, new RectangleF(x, y + 10 + Ring + 22, w, 14), top);
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
        // `--preview` … 最初の取得が終わったらパネルを開く（見た目の確認・スクショ用）
        if (Program.Preview) {
          Program.Preview = false;
          try {
            panel.ShowAt(snap, Cursor.Position);
            // 描いたものをそのまま PNG に落とす（README のスクショと、見た目の確認に使う）
            using (var bmp = new Bitmap(panel.Width, panel.Height)) {
              panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
              bmp.Save(System.IO.Path.Combine(Paths.DataDir, "panel-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
          } catch (Exception ex) { Log.Write("Preview: " + ex.Message); }
        }
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
    if (Config.AgyEnabled)  AddIconSource(iconSrc, "agy",  "Antigravity");
    if (Config.GrokEnabled) AddIconSource(iconSrc, "grok", "Grok Build");
    menu.Items.Add(iconSrc);

    // 任意の2本。入れていない人には見せる意味がないので、既定は OFF
    var extra = new ToolStripMenuItem(S.T("ほかの CLI も見る", "Also watch"));
    var agy = new ToolStripMenuItem("Antigravity (agy)") { Checked = Config.AgyEnabled };
    agy.Click += delegate { Config.AgyEnabled = !Config.AgyEnabled; Reload(true); };
    extra.DropDownItems.Add(agy);
    var grok = new ToolStripMenuItem("Grok Build (grok)") { Checked = Config.GrokEnabled };
    grok.Click += delegate { Config.GrokEnabled = !Config.GrokEnabled; Reload(true); };
    extra.DropDownItems.Add(grok);
    menu.Items.Add(extra);

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

  // 1つ変えるときも全キーを書き直す。書かなかったキーが消えると既定に戻って気づけない
  static void Write(string key, string value) {
    string iconSource = key == "iconSource"   ? value : IconSource;
    string claudeSrc  = key == "claudeSource" ? value : ClaudeSource;
    string agy        = key == "agy"          ? value : (AgyEnabled ? "on" : "off");
    string grok       = key == "grok"         ? value : (GrokEnabled ? "on" : "off");
    try {
      File.WriteAllText(Path,
        "{\r\n" +
        "  \"_comment\": \"iconSource: which provider the tray icon reflects (both|claude|codex|agy|grok). " +
        "claudeSource: where Claude numbers come from (cli|endpoint|statusline). language: auto|ja|en (auto follows Windows). " +
        "agy / grok: on|off, also watch Antigravity CLI / Grok Build (off by default).\",\r\n" +
        "  \"iconSource\": \"" + iconSource + "\",\r\n" +
        "  \"claudeSource\": \"" + claudeSrc + "\",\r\n" +
        "  \"language\": \"" + Language + "\",\r\n" +
        "  \"agy\": \"" + agy + "\",\r\n" +
        "  \"grok\": \"" + grok + "\"\r\n" +
        "}\r\n", new UTF8Encoding(false));
    } catch { }
  }

  // アイコンがどのプロバイダを映すか。"both"（既定）/ "claude" / "codex" / "agy" / "grok"
  // 主に使うツールが人によって違うので、選べるようにしてある
  public static string IconSource {
    get { return Read("iconSource", "both"); }
    set { Write("iconSource", value); }
  }

  // Claude の数値をどこから取るか。
  //   "cli"（既定）  … Claude Code 自身に聞く。認証情報にもネットワークにも触れず、値は本体と同じ
  //   "endpoint"     … 同じ問い合わせ先を直接叩く。速いが認証情報を読む
  //   "statusline"   … statusline のキャッシュを読む。5時間枠と週次だけ
  public static string ClaudeSource {
    get { return Read("claudeSource", "cli"); }
    set { Write("claudeSource", value); }
  }

  // 任意の2本。"on" のときだけ取りに行く（入れていない環境で「応答なし」を並べない）
  public static bool AgyEnabled {
    get { return Read("agy", "off") == "on"; }
    set { Write("agy", value ? "on" : "off"); }
  }
  public static bool GrokEnabled {
    get { return Read("grok", "off") == "on"; }
    set { Write("grok", value ? "on" : "off"); }
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
  public static bool Preview;

  [STAThread]
  static void Main(string[] args) {
    Preview = args.Length > 0 && args[0] == "--preview";
    // `QuotaGauge.exe --once` … 常駐せずに1回だけ取って last-fetch.txt に書いて終わる。
    // 「パネルに何が出るはずか」を目で確かめるための口。トレイをクリックせずに検証できる
    if (args.Length > 0 && args[0] == "--once") {
      var s = Usage.FetchAll();
      var sb = new StringBuilder();
      sb.AppendLine("fetched " + s.FetchedAt.ToString("yyyy-MM-dd HH:mm:ss"));
      foreach (var p in s.Providers) {
        sb.AppendLine("[" + p.Key + "] " + p.Heading + (p.Error != null ? "  ERROR: " + p.Error : ""));
        foreach (var l in p.Limits)
          sb.AppendLine("  " + l.Label + "  " + l.Percent + "%  " + l.Severity + "  " +
                        (l.ResetsAt.HasValue ? l.ResetsAt.Value.ToString("M/d HH:mm") + " " + l.Remaining : ""));
      }
      try { File.WriteAllText(System.IO.Path.Combine(Paths.DataDir, "last-fetch.txt"), sb.ToString(), new UTF8Encoding(false)); } catch { }
      return;
    }

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
