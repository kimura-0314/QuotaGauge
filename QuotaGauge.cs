// QuotaGauge — Claude Code と Codex の利用枠を通知領域から確認する常駐ツール
//
//   左クリック … 使用率のパネルを開く
//   右クリック … 更新 / ログ / 起動時に開始 / 終了
//
// どちらも公式に提供されている経路だけを使う。
//   Claude Code … ステータスラインへ公式に渡される rate_limits を、ローカルのキャッシュ経由で読む
//   Codex       … codex app-server の JSON-RPC `account/rateLimits/read` を呼ぶ
// 認証情報には一切触れないし、外部へ何も送信しない。
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

[assembly: System.Reflection.AssemblyTitle("利用枠ゲージ")]
[assembly: System.Reflection.AssemblyProduct("QuotaGauge")]
[assembly: System.Reflection.AssemblyDescription("Claude Code と Codex の利用枠を通知領域に表示する")]
[assembly: System.Reflection.AssemblyCompany("kimura")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace QuotaGauge {

// ------------------------------------------------------------------ データ
class Limit {
  public string Label = "";
  public int Percent;
  public string Severity = "normal";
  public DateTime? ResetsAt;

  public bool IsCritical { get { return Severity == "critical" || Percent >= 90; } }

  // リセット時刻が過去を指していることがある（提供側の値がそうなっている）。
  // その場合は残り時間を出さない。おかしな値を自信ありげに見せない方がいい
  public bool ResetIsUsable {
    get { return ResetsAt.HasValue && (ResetsAt.Value - DateTime.Now).TotalSeconds > 0; }
  }

  public string Remaining {
    get {
      if (!ResetIsUsable) return "";
      TimeSpan t = ResetsAt.Value - DateTime.Now;
      if (t.TotalHours >= 1) return string.Format("あと {0}時間{1}分", (int)t.TotalHours, t.Minutes);
      return string.Format("あと {0}分", Math.Max(1, (int)t.TotalMinutes));
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
      if (DataTime.HasValue) s += "（" + Ago(DataTime.Value) + "の値）";
      return s;
    }
  }

  // 値が古いほど、他所の表示とズレる。何分前かを見せておく
  public bool IsStale {
    get { return DataTime.HasValue && (DateTime.Now - DataTime.Value).TotalMinutes >= 5; }
  }

  static string Ago(DateTime t) {
    int sec = (int)(DateTime.Now - t).TotalSeconds;
    if (sec < 45) return "たった今";
    int min = (int)Math.Round(sec / 60.0);
    if (min < 60) return min + "分前";
    return (min / 60) + "時間前";
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
    if (minutes >= 60 * 24 * 6.5) return "週次";
    if (minutes >= 60 * 24) return ((int)Math.Round(minutes / (60 * 24))) + "日枠";
    if (minutes >= 60) return ((int)Math.Round(minutes / 60)) + "時間枠";
    return ((int)Math.Round(minutes)) + "分枠";
  }
}

// ------------------------------------------------------------------ Claude Code
// ステータスラインへ公式に渡される rate_limits を、statusline スクリプトが書いたキャッシュから読む。
// 認証情報もネットワークアクセスも使わない。
static class ClaudeApi {
  public static Provider Fetch() {
    return Config.ClaudeSource == "statusline" ? FromStatusLine() : FromEndpoint();
  }

  // --- 既定：Claude Code 自身が使っているのと同じ問い合わせ先から直接読む -----------
  // 5時間枠・週次に加えてモデル別の枠まで返り、リセット時刻も正確。
  // ただし公開されたインターフェースではない（README の注意書きを参照）
  const string Url = "https://api.anthropic.com/api/oauth/usage";
  const string Beta = "oauth-2025-04-20";

  static string CredentialsPath {
    get {
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".claude", ".credentials.json");
    }
  }

  static Provider FromEndpoint() {
    var p = new Provider { Key = "claude", Name = "Claude Code" };
    try {
      string cred = File.ReadAllText(CredentialsPath, Encoding.UTF8);
      var tok = Regex.Match(cred, "\"accessToken\"\\s*:\\s*\"([^\"]+)\"");
      if (!tok.Success) throw new Exception("Claude Code にログインしていません");

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

      foreach (var obj in Json.Objects(body, "limits")) {
        var l = new Limit();
        l.Percent  = (int)Math.Round(Json.Num(obj, "percent") ?? 0);
        l.Severity = Json.Str(obj, "severity") ?? "normal";
        l.ResetsAt = Json.Iso(obj, "resets_at");

        string kind = Json.Str(obj, "kind") ?? "";
        var scope = Regex.Match(obj, "\"scope\"\\s*:\\s*\\{.*?\"display_name\"\\s*:\\s*\"([^\"]+)\"",
                                RegexOptions.Singleline);
        if (scope.Success)             l.Label = "週次（" + scope.Groups[1].Value + "）";
        else if (kind == "session")    l.Label = "5時間枠";
        else if (kind == "weekly_all") l.Label = "週次（全体）";
        else                           l.Label = kind;

        p.Limits.Add(l);
      }
      p.DataTime = DateTime.Now;
      if (p.Limits.Count == 0) p.Error = "利用枠の情報が空でした";
    } catch (WebException wex) {
      var r = wex.Response as HttpWebResponse;
      p.Error = r != null ? "取得できません (HTTP " + (int)r.StatusCode + ")" : "取得できません: " + wex.Message;
    } catch (Exception ex) {
      p.Error = ex.Message;
    }
    return p;
  }

  // --- もう一方：ステータスライン経由 ------------------------------------------
  // 公開された経路だけで動くが、渡ってくる値の精度は上に劣る
  // （5時間枠のリセット時刻が過去を指すことがある／モデル別の枠は含まれない）
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
        p.Error = "ステータスラインの設定が必要です（README参照）";
        return p;
      }
      string json = File.ReadAllText(CachePath, Encoding.UTF8);
      AddCached(p, json, "five_hour", "5時間枠");
      AddCached(p, json, "seven_day", "週次");

      double? upd = Json.Num(json, "updated_at");
      if (upd.HasValue) p.DataTime = Json.FromUnix(upd.Value);

      if (p.Limits.Count == 0) p.Error = "利用枠の情報がありません";
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
      if (res == null) { p.Error = "codex app-server から応答がありません"; return p; }

      string rl = Json.Object(res, "rateLimits");
      if (rl == null) { p.Error = "利用枠の情報がありません"; return p; }

      p.Note = Json.Str(rl, "planType");
      Add(p, Json.Object(rl, "primary"));
      Add(p, Json.Object(rl, "secondary"));
      p.DataTime = DateTime.Now;

      if (p.Limits.Count == 0) p.Error = "利用枠の情報が空でした";
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
    l.Label = win.HasValue ? Json.WindowLabel(win.Value) : "利用枠";

    double? reset = Json.Num(obj, "resetsAt");
    if (reset.HasValue) l.ResetsAt = Json.FromUnix(reset.Value);

    p.Limits.Add(l);
  }

  static string Call() {
    var psi = new ProcessStartInfo("cmd.exe", "/c codex app-server");
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
        "{\"name\":\"QuotaGauge\",\"title\":\"QuotaGauge\",\"version\":\"2.0.0\"}}}");
      proc.StandardInput.Flush();
      proc.StandardInput.WriteLine(
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
      proc.StandardInput.Flush();

      // 通知が混ざって流れてくるので、目的の id の行が来るまで読み進める
      for (int i = 0; i < 200; i++) {
        string line = proc.StandardOutput.ReadLine();
        if (line == null) break;
        if (line.Contains("\"id\":2")) return line;
      }
      return null;
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
    refreshBtn.Text = "更新";
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

    Deactivate += delegate { Hide(); };
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
    refreshBtn.Text = busy ? "更新中" : "更新";
    refreshBtn.Refresh();
  }

  public void ShowAt(Snapshot s, Point anchor) {
    snap = s;

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
      g.DrawString("利用枠", fTitle, b, 16, 14);

    int y = 44;

    if (snap == null) {
      using (var b = new SolidBrush(Palette.SubText))
        g.DrawString("読み込み中…", fLabel, b, 16, y + 8);
    } else {
      foreach (var pv in snap.Providers) {
        // 値が古いときは見出しの色を変えて、他所の表示とのズレに気づけるようにする
        using (var b = new SolidBrush(pv.IsStale ? Palette.BarWarn : Palette.Heading))
          g.DrawString(pv.Heading, fHeading, b, 16, y);
        y += HeadH;

        if (pv.Limits.Count == 0) {
          using (var b = new SolidBrush(Palette.SubText))
            g.DrawString(pv.Error ?? "情報なし", fSub, b, 16, y);
          y += RowH;
          continue;
        }
        foreach (var l in pv.Limits) { DrawRow(g, l, y); y += RowH; }
      }
    }

    using (var b = new SolidBrush(Palette.SubText))
      g.DrawString(snap == null ? "" : "最終取得 " + snap.FetchedAt.ToString("HH:mm:ss"),
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
    string sub = "残り " + Math.Max(0, 100 - l.Percent) + "%";
    if (l.ResetIsUsable)
      sub += " ・ " + l.Remaining + "（" + l.ResetsAt.Value.ToString("M/d HH:mm") + "）";
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
    ni.Icon = SystemIcons.Application;
    ni.Text = "利用枠ゲージ";
    ni.ContextMenuStrip = menu;
    ni.Visible = true;
    ni.MouseClick += OnClick;

    panel = new QuotaPanel();
    panel.RefreshRequested += delegate { Reload(); };
    // 別スレッドの取得結果をUIへ戻すため、表示前にウィンドウハンドルを作っておく
    { IntPtr dummy = panel.Handle; }

    timer = new System.Windows.Forms.Timer();
    timer.Interval = 3 * 60 * 1000;
    timer.Tick += delegate { Reload(); };
    timer.Start();

    UpdateIcon();
    Reload();
  }

  void Reload() {
    try { panel.SetBusy(true); } catch { }

    var t = new Thread(delegate () {
      Snapshot s;
      try { s = Usage.FetchAll(); }
      catch (Exception ex) {
        s = new Snapshot { FetchedAt = DateTime.Now };
        Log.Write("取得中の例外: " + ex.Message);
      }

      MethodInvoker apply = delegate {
        snap = s;
        try { panel.UpdateSnapshot(s); panel.SetBusy(false); } catch { }
        UpdateIcon();
        foreach (var p in s.Providers)
          if (p.Error != null) Log.Write(p.Name + ": " + p.Error);
      };
      try {
        if (panel.IsHandleCreated) panel.BeginInvoke(apply);
        else apply();
      } catch (Exception ex) { Log.Write("Reload: " + ex.Message); }
    });
    t.IsBackground = true;
    t.Start();
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
      string tip = sb.Length == 0 ? "利用枠ゲージ" : sb.ToString();
      ni.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
    } catch (Exception ex) { Log.Write("UpdateIcon: " + ex.Message); }
  }

  void OnClick(object sender, MouseEventArgs e) {
    if (e.Button != MouseButtons.Left) return;
    if (panel.Visible) { panel.Hide(); return; }
    panel.ShowAt(snap, Cursor.Position);
  }

  void BuildMenu(object sender, System.ComponentModel.CancelEventArgs e) {
    menu.Items.Clear();

    var open = new ToolStripMenuItem("利用枠を見る");
    open.Click += delegate { panel.ShowAt(snap, Cursor.Position); };
    menu.Items.Add(open);

    var reload = new ToolStripMenuItem("今すぐ更新");
    reload.Click += delegate { Reload(); };
    menu.Items.Add(reload);

    var log = new ToolStripMenuItem("ログを見る");
    log.Enabled = File.Exists(Log.Path);
    log.Click += delegate {
      try { Process.Start("notepad.exe", Log.Path); } catch { }
    };
    menu.Items.Add(log);

    menu.Items.Add(new ToolStripSeparator());

    // 主に使うツールは人によって違うので、アイコンが映す対象を選べるようにする
    var iconSrc = new ToolStripMenuItem("アイコンに出す対象");
    AddIconSource(iconSrc, "both",   "厳しい方");
    AddIconSource(iconSrc, "claude", "Claude Code");
    AddIconSource(iconSrc, "codex",  "Codex");
    menu.Items.Add(iconSrc);

    var claudeSrc = new ToolStripMenuItem("Claude の取得元");
    AddClaudeSource(claudeSrc, "endpoint",   "Claude Code と同じ経路（正確）");
    AddClaudeSource(claudeSrc, "statusline", "ステータスライン経由（公開経路のみ）");
    menu.Items.Add(claudeSrc);

    var startup = new ToolStripMenuItem("Windows起動時に開始");
    startup.Checked = RunAtStartup;
    startup.Click += delegate { RunAtStartup = !RunAtStartup; };
    menu.Items.Add(startup);

    menu.Items.Add(new ToolStripSeparator());

    var quit = new ToolStripMenuItem("終了");
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
    item.Click += delegate { Config.ClaudeSource = key; Reload(); };
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

static class Config {
  public static string Path {
    get {
      return System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Application.ExecutablePath), "config.json");
    }
  }

  static string Read(string key, string fallback) {
    try {
      if (!File.Exists(Path)) return fallback;
      var m = Regex.Match(File.ReadAllText(Path, Encoding.UTF8),
                          "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
      return (m.Success && m.Groups[1].Value.Length > 0) ? m.Groups[1].Value : fallback;
    } catch { return fallback; }
  }

  static void Write(string iconSource, string claudeSource) {
    try {
      File.WriteAllText(Path,
        "{\r\n" +
        "  \"_comment\": \"iconSource: which provider the tray icon reflects (both|claude|codex). " +
        "claudeSource: where Claude numbers come from (endpoint|statusline).\",\r\n" +
        "  \"iconSource\": \"" + iconSource + "\",\r\n" +
        "  \"claudeSource\": \"" + claudeSource + "\"\r\n" +
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
  //   "endpoint"（既定）  … Claude Code と同じ問い合わせ先。モデル別の枠まで出て、リセット時刻も正確
  //   "statusline"        … 公開された経路だけを使う。値の精度は落ちる
  public static string ClaudeSource {
    get { return Read("claudeSource", "endpoint"); }
    set { Write(IconSource, value); }
  }
}

static class Log {
  public static string Path {
    get {
      return System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Application.ExecutablePath), "quotagauge.log");
    }
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
        Log.Write("例外: " + ev.Exception);
      };
      AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs ev) {
        Log.Write("未処理例外: " + ev.ExceptionObject);
      };

      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new TrayApp());
      GC.KeepAlive(mutex);
    }
  }
}

}
