#!/usr/bin/python3
# -*- coding: utf-8 -*-
# <xbar.title>QuotaGauge (残量チラ見)</xbar.title>
# <xbar.version>v0.1.0</xbar.version>
# <xbar.author>kimura</xbar.author>
# <xbar.author.github>kimura-0314</xbar.author.github>
# <xbar.desc>Claude Code, Codex, Antigravity and Grok Build usage limits in the menu bar. Asks each CLI itself.</xbar.desc>
# <xbar.abouturl>https://github.com/kimura-0314/QuotaGauge</xbar.abouturl>
# <swiftbar.hideRunInTerminal>true</swiftbar.hideRunInTerminal>
# <swiftbar.hideLastUpdated>true</swiftbar.hideLastUpdated>
#
# Windows 版 QuotaGauge.cs の取得部分を、SwiftBar / xbar のプラグインに移したもの。
#   Claude Code … `claude` を stream-json で起動して control_request {subtype:"get_usage"} を1行流す
#   Codex       … `codex app-server` の JSON-RPC `account/rateLimits/read`
# どちらも CLI 自身に聞くので、認証情報にもネットワークにも触らない。モデルも呼ばれない。
#
# ⚠ このファイルの stdout がそのままメニューになる。CLI の生の出力は絶対に print しない
#   （app-server などは設定の環境変数を流してくることがある）
#
# 依存なし。macOS 標準の /usr/bin/python3（3.9）で動くように書いている。

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
from datetime import datetime, timezone

# ------------------------------------------------------------------ 表示言語
def _detect_ja():
    forced = os.environ.get("QUOTAGAUGE_LANG", "").lower()
    if forced in ("ja", "en"):
        return forced == "ja"
    if sys.platform == "darwin":
        try:
            out = subprocess.run(["/usr/bin/defaults", "read", "-g", "AppleLanguages"],
                                 capture_output=True, text=True, timeout=3).stdout
            for line in out.splitlines():
                m = re.match(r'\s*"?([A-Za-z]{2})\b', line)
                if m:
                    return m.group(1).lower() == "ja"
        except Exception:
            pass
    return os.environ.get("LANG", "").lower().startswith("ja")

JA = _detect_ja()

def T(ja, en):
    return ja if JA else en

# ------------------------------------------------------------------ PATH
# SwiftBar / xbar から動くときはログインシェルの設定を読まないので、
# Homebrew や nvm で入れた claude / codex（と npm 版が使う node）が PATH に無いことが多い。
# よくある置き場所を足して探し、それでも見つからない・起動できない時だけログインシェルの PATH を足す
def _login_shell_path():
    shell = os.environ.get("SHELL") or "/bin/zsh"
    if not os.path.exists(shell):
        return ""
    try:
        out = subprocess.run([shell, "-lic", "echo __QG__$PATH"], capture_output=True, text=True,
                             timeout=5, stdin=subprocess.DEVNULL).stdout
        # rc が改行なしで何か出していても拾えるよう、行頭に限らず探す
        m = re.search(r"__QG__(.*)", out)
        if m:
            return m.group(1).strip()
    except Exception:
        pass
    return ""

def _build_path():
    home = os.path.expanduser("~")
    extra = [
        "/opt/homebrew/bin", "/usr/local/bin",
        os.path.join(home, ".local/bin"), os.path.join(home, ".claude/local"),
        os.path.join(home, ".npm-global/bin"), os.path.join(home, ".volta/bin"),
        os.path.join(home, ".bun/bin"), os.path.join(home, ".asdf/shims"),
        os.path.join(home, ".grok/bin"),
    ]
    nvm = os.path.join(home, ".nvm/versions/node")
    if os.path.isdir(nvm):
        def ver(v):
            return [int(x) for x in re.findall(r"\d+", v)]
        for v in sorted(os.listdir(nvm), key=ver, reverse=True):
            extra.append(os.path.join(nvm, v, "bin"))
    parts = os.environ.get("PATH", "").split(os.pathsep)
    seen, out = set(), []
    for p in parts + extra:
        if p and p not in seen:
            seen.add(p)
            out.append(p)
    return os.pathsep.join(out)

ENV = dict(os.environ)
ENV["PATH"] = _build_path()
# telegram プラグインが入っている環境で、常駐中のポーラーを止めさせない（Windows 版と同じ）
ENV["TELEGRAM_STATE_DIR"] = os.path.join(tempfile.gettempdir(), "quotagauge-no-telegram")
# 作業ディレクトリは固定する（プラグインのフォルダに同名のファイルを置かれても拾わないように）
WORKDIR = os.path.expanduser("~")

START_FAILED = object()
_login_tried = False
_login_lock = threading.Lock()

def _merge_login_path():
    # Claude と Codex を並列に取るので、ログインシェルを2回走らせないよう鍵をかける
    global _login_tried
    with _login_lock:
        if _login_tried:
            return
        _login_tried = True
        login = _login_shell_path()
        if login and ":" in login:
            ENV["PATH"] = login + os.pathsep + ENV["PATH"]

def which(name, optional=False):
    # ログインシェルを動かすのは、よくある置き場所で見つからない時だけ（3分ごとに rc を走らせない）。
    # agy / grok は入れていない人の方が多いので、それだけのためにはログインシェルを動かさない
    found = shutil.which(name, path=ENV["PATH"])
    if found or sys.platform != "darwin" or optional:
        return found
    _merge_login_path()
    return shutil.which(name, path=ENV["PATH"])

# ------------------------------------------------------------------ 子プロセス
# stdin に何行か流し、match が真になる行が来るまで stdout を読む。時間切れなら殺す
def read_until(argv, stdin_lines, match, timeout, close_stdin):
    try:
        proc = subprocess.Popen(argv, cwd=WORKDIR, env=ENV, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    except OSError:
        return START_FAILED
    found = []

    def reader():
        try:
            for raw in proc.stdout:
                line = raw.decode("utf-8", "replace")
                if match(line):
                    found.append(line)
                    return
        except Exception:
            pass

    t = threading.Thread(target=reader, daemon=True)
    t.start()
    try:
        for l in stdin_lines:
            proc.stdin.write((l + "\n").encode("utf-8"))
            proc.stdin.flush()
        if close_stdin:
            proc.stdin.close()
    except Exception:
        pass
    t.join(timeout)
    try:
        if not proc.stdin.closed:
            proc.stdin.close()
    except Exception:
        pass
    try:
        proc.wait(5)
    except Exception:
        # npm 版 codex は SIGTERM を本体へ転送するが SIGKILL は転送しない。先に TERM を送る
        proc.terminate()
        try:
            proc.wait(2)
        except Exception:
            proc.kill()
            proc.wait()
    if found:
        return found[0]
    # npm 版は `#!/usr/bin/env node`。node が PATH に無いと env が 127 で終わる
    return START_FAILED if proc.returncode == 127 else None

def run_cli(argv, stdin_lines, match, timeout, close_stdin):
    line = read_until(argv, stdin_lines, match, timeout, close_stdin)
    if line is START_FAILED and sys.platform == "darwin":
        # node などが見つからない時だけ、ログインシェルの PATH を足してもう一度
        _merge_login_path()
        line = read_until(argv, stdin_lines, match, timeout, close_stdin)
    return line

def start_failed_msg(name):
    return T("%s を起動できませんでした（node が PATH に無い可能性）" % name,
             "Could not start %s (node may be missing from PATH)" % name)

# ------------------------------------------------------------------ 時刻
def parse_iso(s):
    if not isinstance(s, str) or not s:
        return None
    s = s.strip().replace("Z", "+00:00")
    # 3.9 の fromisoformat は小数秒が6桁でないと読めない
    m = re.match(r"^(.*?\d{2}:\d{2}:\d{2})(\.\d+)?(.*)$", s)
    if m:
        frac = (m.group(2) or "")[:7]
        if frac:
            frac = frac.ljust(7, "0")
        s = m.group(1) + frac + m.group(3)
    try:
        dt = datetime.fromisoformat(s)
    except ValueError:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone()

def from_unix(sec):
    try:
        return datetime.fromtimestamp(float(sec), tz=timezone.utc).astimezone()
    except Exception:
        return None

def window_label(minutes):
    if minutes >= 60 * 24 * 6.5:
        return T("週次", "Weekly")
    if minutes >= 60 * 24:
        return str(int(round(minutes / 1440))) + T("日枠", "-day")
    if minutes >= 60:
        return str(int(round(minutes / 60))) + T("時間枠", "-hour")
    return str(int(round(minutes))) + T("分枠", "-min")

def limit(label, percent, resets_at, critical=False):
    return {"label": label, "percent": int(round(percent)), "resets_at": resets_at, "critical": critical}

# ------------------------------------------------------------------ Claude Code
def fetch_claude():
    p = {"key": "claude", "name": "Claude Code", "note": None, "limits": [], "error": None}
    exe = which("claude")
    if not exe:
        p["error"] = T("claude が見つかりません（インストールと PATH を確認）",
                       "claude was not found (check that it is installed and on PATH)")
        return p
    argv = [exe, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            # MCP の読み込みが起動時間の半分を占めるので落とす。hook も要らない
            "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}', "--settings", '{"hooks":{}}']
    # skip_behaviors＝直近7日の会話ログを読む集計を飛ばす。付けないと 2.1.303 では1分を超える
    req = '{"type":"control_request","request_id":"1","request":{"subtype":"get_usage","skip_behaviors":true}}'
    line = run_cli(argv, [req], lambda l: '"control_response"' in l, 60, close_stdin=True)
    if line is START_FAILED:
        p["error"] = start_failed_msg("claude")
        return p
    if line is None:
        p["error"] = T("claude から応答がありません（ログイン状態を確認）",
                       "No response from claude (check that you are signed in)")
        return p
    try:
        d = json.loads(line)
    except ValueError:
        p["error"] = T("claude の返事を読めませんでした", "Could not parse the reply from claude")
        return p
    resp = d.get("response") or {}
    if resp.get("subtype") == "error":
        p["error"] = resp.get("error") or T("利用枠を取得できませんでした", "Could not read the usage data")
        return p
    body = resp.get("response") or resp
    p["note"] = _find(body, "subscription_type")
    p["limits"] = _claude_limits(body)
    if not p["limits"]:
        p["error"] = T("利用枠の情報が空でした", "The usage data was empty")
    return p

def _find(obj, key):
    # 返事の形は版によって入れ子が変わる。深さを問わず最初の値を拾う
    if isinstance(obj, dict):
        if key in obj and obj[key] is not None:
            return obj[key]
        for v in obj.values():
            r = _find(v, key)
            if r is not None:
                return r
    elif isinstance(obj, list):
        for v in obj:
            r = _find(v, key)
            if r is not None:
                return r
    return None

def _claude_limits(body):
    out = []
    for o in _find(body, "limits") or []:
        if not isinstance(o, dict):
            continue
        kind = o.get("kind") or ""
        # モデル別の枠（Fable など）は scope の奥に display_name を持っている
        name = _find(o.get("scope"), "display_name")
        if name:
            label = T("週次（", "Weekly (") + name + T("）", ")")
        elif kind == "session":
            label = T("5時間枠", "5-hour")
        elif kind == "weekly_all":
            label = T("週次（全体）", "Weekly (all)")
        else:
            label = kind
        out.append(limit(label, o.get("percent") or 0, parse_iso(o.get("resets_at")),
                         o.get("severity") == "critical"))
    if out:
        return out

    # 2.1.303 は limits の配列を付けない返事が混ざる。その時は rate_limits の各枠から読む
    rl = _find(body, "rate_limits")
    if not isinstance(rl, dict):
        return out
    for key, label in (("five_hour", T("5時間枠", "5-hour")),
                       ("seven_day", T("週次（全体）", "Weekly (all)")),
                       ("seven_day_opus", T("週次（Opus）", "Weekly (Opus)")),
                       ("seven_day_sonnet", T("週次（Sonnet）", "Weekly (Sonnet)"))):
        o = rl.get(key)
        if isinstance(o, dict) and o.get("utilization") is not None:
            out.append(limit(label, o["utilization"], parse_iso(o.get("resets_at"))))
    for o in rl.get("model_scoped") or []:
        if isinstance(o, dict) and o.get("utilization") is not None and o.get("display_name"):
            out.append(limit(T("週次（", "Weekly (") + o["display_name"] + T("）", ")"),
                             o["utilization"], parse_iso(o.get("resets_at"))))
    return out

# ------------------------------------------------------------------ Codex
def fetch_codex():
    p = {"key": "codex", "name": "Codex", "note": None, "limits": [], "error": None}
    exe = which("codex")
    if not exe:
        p["error"] = T("codex が見つかりません（インストールと PATH を確認）",
                       "codex was not found (check that it is installed and on PATH)")
        return p
    lines = [
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":'
        '{"name":"QuotaGauge","title":"QuotaGauge","version":"mac-0.1.0"}}}',
        '{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":{}}',
    ]
    line = run_cli([exe, "app-server"], lines, _is_id2, 15, close_stdin=False)
    if line is START_FAILED:
        p["error"] = start_failed_msg("codex")
        return p
    if line is None:
        p["error"] = T("codex app-server から応答がありません", "No response from codex app-server")
        return p
    try:
        d = json.loads(line)
    except ValueError:
        p["error"] = T("codex の返事を読めませんでした", "Could not parse the reply from codex")
        return p
    if d.get("error"):
        err = d["error"]
        p["error"] = (err.get("message") if isinstance(err, dict) else str(err)) or T("利用枠を取得できませんでした", "Could not read the usage data")
        return p
    res = d.get("result") or {}
    rl = res.get("rateLimits")
    if not isinstance(rl, dict):
        p["error"] = T("利用枠の情報がありません", "No usage data")
        return p

    note = rl.get("planType") or ""
    _codex_add(p, rl.get("primary"), None)
    _codex_add(p, rl.get("secondary"), None)

    # 手動リセットの残り回数（Plus の「あと N 回リセット可能」）
    credits = res.get("rateLimitResetCredits") or {}
    n = int(credits.get("availableCount") or 0) if isinstance(credits, dict) else 0
    if n > 0:
        note += T(" ・ リセット残 %d" % n, " · %d reset%s left" % (n, "" if n == 1 else "s"))

    # 主枠とは別の limitId ごとの枠（gpt-reserve など）
    by_id = res.get("rateLimitsByLimitId") or {}
    if isinstance(by_id, dict):
        for lid, other in by_id.items():
            if not isinstance(other, dict) or lid == rl.get("limitId"):
                continue
            name = other.get("limitName") or lid
            if name == "gpt-reserve":
                name = "Luna Reserve"   # OpenAI ヘルプでの呼び名
            _codex_add(p, other.get("primary"), name)
            _codex_add(p, other.get("secondary"), name)

    p["note"] = note or None
    if not p["limits"]:
        p["error"] = T("利用枠の情報が空でした", "The usage data was empty")
    return p

def _is_id2(line):
    # 通知の奥にある "id": 2 を拾わないよう、一番外の id だけを見る
    if '"id"' not in line:
        return False
    try:
        return json.loads(line).get("id") == 2
    except Exception:
        return False

def _codex_add(p, o, name):
    if not isinstance(o, dict) or o.get("usedPercent") is None:
        return
    win = o.get("windowDurationMins")
    label = name or (window_label(win) if win is not None else T("利用枠", "Usage"))
    reset = from_unix(o["resetsAt"]) if o.get("resetsAt") is not None else None
    p["limits"].append(limit(label, o["usedPercent"], reset))

# ------------------------------------------------------------------ agy（Antigravity CLI）
# print モードの `/quota` は turn を起こさずに答える（agy 1.1.11 以降）。
# 週次のグループ（Gemini 系／Claude・GPT 系）ごとに remaining_fraction と reset_time を返す
def fetch_agy():
    exe = which("agy", optional=True)
    if not exe:
        return None   # 入れていない人には出さない
    p = {"key": "agy", "name": "Antigravity", "note": None, "limits": [], "error": None}
    line = run_cli([exe, "--output-format", "json", "--print", "/quota"], [],
                   lambda l: '"groups"' in l, 40, close_stdin=True)
    if line is START_FAILED:
        p["error"] = start_failed_msg("agy")
        return p
    if line is None:
        p["error"] = T("agy から応答がありません（ログイン状態を確認）",
                       "No response from agy (check that you are signed in)")
        return p
    try:
        d = json.loads(line)
    except ValueError:
        p["error"] = T("agy の返事を読めませんでした", "Could not parse the reply from agy")
        return p
    # turn が走ったなら /quota として扱われていない
    if (_find(d, "num_turns") or 0) > 0:
        p["error"] = T("agy が /quota を実行しませんでした（版が古い？）", "agy did not answer /quota (old version?)")
        return p
    for grp in _find(d, "groups") or []:
        if not isinstance(grp, dict):
            continue
        name = str(grp.get("name") or "")
        short = "Gemini" if "gemini" in name.lower() else "Claude/GPT" if "claude" in name.lower() else name
        for b in grp.get("buckets") or []:
            if not isinstance(b, dict) or b.get("remaining_fraction") is None:
                continue
            win = b.get("window") or ""
            # 週次しか無いので「週次」は書かず、モデル群の名前だけにする
            label = short if win == "weekly" else "%s (%s)" % (short, win)
            try:
                pct = (1 - float(b["remaining_fraction"])) * 100
            except (TypeError, ValueError):
                continue
            p["limits"].append(limit(label, pct, parse_iso(b.get("reset_time"))))
    if not p["limits"]:
        p["error"] = T("利用枠の情報が空でした", "The usage data was empty")
    return p

# ------------------------------------------------------------------ grok（Grok Build）
# `grok agent stdio` は ACP（JSON-RPC）で話す。拡張メソッドは `_x.ai/…`（先頭アンダースコア）。
# `_x.ai/billing` が creditUsagePercent（1.0 = 1%）と週の期間を返す。
# ⚠ initialize 直後の通知に MCP 設定の環境変数（トークン類）がそのまま流れてくる。生の行は絶対に出さない
def fetch_grok():
    exe = which("grok", optional=True)
    if not exe:
        return None
    p = {"key": "grok", "name": "Grok Build", "note": None, "limits": [], "error": None}
    lines = [
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,'
        '"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false}}}',
        '{"jsonrpc":"2.0","id":2,"method":"_x.ai/billing","params":{}}',
    ]
    line = run_cli([exe, "agent", "stdio"], lines, _is_id2, 40, close_stdin=False)
    if line is START_FAILED:
        p["error"] = start_failed_msg("grok")
        return p
    if line is None:
        p["error"] = T("grok から応答がありません（ログイン状態を確認）",
                       "No response from grok (check that you are signed in)")
        return p
    try:
        d = json.loads(line)
    except ValueError:
        p["error"] = T("grok の返事を読めませんでした", "Could not parse the reply from grok")
        return p
    if d.get("error"):
        err = d["error"]
        p["error"] = (err.get("message") if isinstance(err, dict) else str(err)) or T("利用枠を取得できませんでした", "Could not read the usage data")
        return p
    res = d.get("result") or {}
    cfg = _find(res, "config")
    if not isinstance(cfg, dict):
        p["error"] = T("利用枠の情報がありません", "No usage data")
        return p
    tier = _find(res, "subscription_tier")
    p["note"] = tier if isinstance(tier, str) else None
    used = cfg.get("creditUsagePercent")
    if used is not None:
        period = cfg.get("currentPeriod") if isinstance(cfg.get("currentPeriod"), dict) else {}
        label = T("週次クレジット", "Weekly credits") if "WEEKLY" in str(period.get("type") or "") \
            else T("クレジット", "Credits")
        try:
            p["limits"].append(limit(label, float(used), parse_iso(period.get("end"))))
        except (TypeError, ValueError):
            pass
    # 従量（on-demand）を使う設定なら、その消化も1本出す
    cap, od = cfg.get("onDemandCap"), cfg.get("onDemandUsed")
    cap_v = cap.get("val") if isinstance(cap, dict) else None
    od_v = od.get("val") if isinstance(od, dict) else None
    try:
        if cap_v and float(cap_v) > 0 and od_v is not None:
            p["limits"].append(limit(T("従量分", "On-demand"), float(od_v) * 100 / float(cap_v), None))
    except (TypeError, ValueError):
        pass
    if not p["limits"]:
        p["error"] = T("利用枠の情報が空でした", "The usage data was empty")
    return p

# ------------------------------------------------------------------ 表示
# grok の墨色は暗いメニューで読めないので、明暗で色を分ける
ACCENT = {"claude": "#D97757", "codex": "#10A37F", "agy": "#4285F4", "grok": "#374151,#D1D5DB"}
WARN, CRIT = "#D97706", "#DC3E3E"
MUTED = "#8E8E93"

def color_for(pct, critical=False):
    return CRIT if critical or pct >= 90 else WARN if pct >= 70 else None

def bar(pct, width=10):
    filled = max(0, min(width, int(round(pct * width / 100.0))))
    return "█" * filled + "░" * (width - filled)

def remaining(dt, now):
    if dt is None:
        return ""
    # 過去を指すリセット時刻は出さない（古い値を自信ありげに見せない）
    if (dt - now).total_seconds() <= 0:
        return ""
    sec = (dt - now).total_seconds() + 30   # 数十ミリ秒手前で返ることがあるので分に丸める
    h, m = int(sec // 3600), int(sec % 3600 // 60)
    if h >= 24:
        rest = T("あと %d日%d時間" % (h // 24, h % 24), "resets in %dd %dh" % (h // 24, h % 24))
    elif h >= 1:
        rest = T("あと %d時間%d分" % (h, m), "resets in %dh %dm" % (h, m))
    else:
        rest = T("あと %d分" % max(1, m), "resets in %dm" % max(1, m))
    stamp = datetime.fromtimestamp(dt.timestamp() + 30).astimezone().strftime("%-m/%-d %H:%M") \
        if sys.platform != "win32" else datetime.fromtimestamp(dt.timestamp() + 30).strftime("%m/%d %H:%M")
    return rest + " · " + stamp

def clean(s):
    # メニュー記法の区切り文字が値に混ざると表示が崩れる
    return str(s).replace("|", "/").replace("\r", " ").replace("\n", " ").lstrip("-")

def render(providers):
    now = datetime.now().astimezone()
    pcts = [l["percent"] for p in providers for l in p["limits"]]
    worst = max(pcts) if pcts else None
    any_crit = any(l["critical"] for p in providers for l in p["limits"])
    out = []
    if worst is None:
        out.append("— | sfimage=gauge.with.dots.needle.0percent")
    else:
        c = color_for(worst, any_crit)
        out.append("%d%% | sfimage=gauge.with.dots.needle.50percent%s" % (worst, (" color=" + c) if c else ""))
    out.append("---")
    for p in providers:
        head = str(p["name"]) + ((" · " + str(p["note"])) if p.get("note") else "")
        out.append("%s | color=%s size=13" % (clean(head), ACCENT.get(p["key"], MUTED)))
        if p["error"]:
            out.append("%s | color=%s size=12" % (clean(p["error"]), CRIT))
        for l in p["limits"]:
            c = color_for(l["percent"], l["critical"])
            out.append("%s  %3d%%  %s | font=Menlo size=12%s" % (
                bar(l["percent"]), l["percent"], clean(l["label"]), (" color=" + c) if c else ""))
            rest = remaining(l["resets_at"], now)
            if rest:
                out.append("%s | font=Menlo size=11 trim=false color=%s" % ("            " + clean(rest), MUTED))
        out.append("---")
    out.append(T("取得 ", "Fetched ") + now.strftime("%H:%M") + " | size=11 color=" + MUTED)
    out.append(T("今すぐ更新", "Refresh now") + " | refresh=true")
    out.append("QuotaGauge on GitHub | href=https://github.com/kimura-0314/QuotaGauge")
    return "\n".join(out)

def main():
    # agy / grok は入っている時だけ出す（見つからなければ None が返って行ごと消える）
    jobs = [fetch_claude, fetch_codex, fetch_agy, fetch_grok]
    names = ["Claude Code", "Codex", "Antigravity", "Grok Build"]
    results = [None] * len(jobs)

    def run(i):
        try:
            results[i] = jobs[i]()
        except Exception as ex:
            results[i] = {"key": "?", "name": names[i], "note": None, "limits": [], "error": str(ex)}

    threads = [threading.Thread(target=run, args=(i,), daemon=True) for i in range(len(jobs))]
    for t in threads:
        t.start()
    for t in threads:
        t.join(90)
    providers = [r for r in results if r is not None]
    if "--json" in sys.argv:
        # 動作確認用。値だけを出す（CLI の生の出力は含まない）
        print(json.dumps(providers, default=lambda o: o.isoformat(), ensure_ascii=False, indent=2))
        return
    print(render(providers))

if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
