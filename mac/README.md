# QuotaGauge for Mac (preview) ・ 残量チラ見 Mac 版（お試し）

[日本語は下](#日本語)

> **Untested on a real Mac.** I am on Windows. The part that asks `claude` and `codex` for their
> numbers was run on Windows and matched the Windows app exactly, but the menu bar side has never
> been seen on a Mac. If you try it, please tell me what happened —
> [X @kimura_0314](https://x.com/kimura_0314) or a GitHub issue.
>
> If you want something mature, use [CodexBar](https://github.com/steipete/CodexBar). It does far more.

A menu bar plugin for [SwiftBar](https://github.com/swiftbar/SwiftBar) (or xbar) that shows your
Claude Code and Codex usage limits — plus Antigravity (`agy`) and Grok Build (`grok`) when they are
installed — the same numbers as the Windows tray app, got the same way: it asks each CLI itself
(`claude` → `get_usage`, `codex app-server` → `account/rateLimits/read`, `agy --print /quota`,
`grok agent stdio` → `_x.ai/billing`).
No credential files are read, nothing is sent anywhere, no model is called.

## Install

1. Install SwiftBar: `brew install --cask swiftbar` (or download it from its GitHub page). Pick a
   plugin folder when it asks.
2. Download `quotagauge.3m.py` into that folder and make it executable (replace the folder path
   with yours):

   ```bash
   curl -fsSL https://raw.githubusercontent.com/kimura-0314/QuotaGauge/main/mac/quotagauge.3m.py -o ~/path/to/plugin-folder/quotagauge.3m.py && chmod +x ~/path/to/plugin-folder/quotagauge.3m.py
   ```

3. In the SwiftBar menu, choose **Refresh All**. A percentage appears in the menu bar; click it for
   the details. It refreshes every 3 minutes (the `3m` in the file name).

Needs: macOS with `/usr/bin/python3` (if macOS offers to install the Command Line Tools, accept),
and `claude` / `codex` installed and signed in. Either one alone is fine. `agy` and `grok` show up
only if they are found; nothing to switch on.

## If it says "not found"

SwiftBar does not load your shell profile, so tools installed with Homebrew, nvm, etc. may not be on
its PATH. The plugin adds the usual places (`/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`,
`~/.nvm/...`, `~/.volta/bin`, `~/.grok/bin`), and only if that is not enough it asks your login shell for its PATH.
"Could not start … (node may be missing)" means the CLI was found but the `node` it needs was not.
Either way, tell me where `which claude`, `which codex` and `which node` point.
If `agy` or `grok` does not show up, check that `which agy` / `which grok` is in one of the usual
places above — for these two the plugin does not ask your login shell.

To check from Terminal: `./quotagauge.3m.py --json` prints only the parsed numbers.

Language follows the first entry of your macOS languages. Force it with `QUOTAGAUGE_LANG=ja` or `en`.

---

## 日本語

> **実機の Mac では一度も動かしていない。** 作った人は Windows しか持っていない。
> `claude` と `codex` に数字を聞く部分は Windows で動かして Windows 版と同じ値になったが、
> メニューバーに出る部分は Mac で見ていない。試した人は、どうなったかを
> [X @kimura_0314](https://x.com/kimura_0314) か GitHub の Issue で教えてほしい。
>
> ちゃんとしたものが欲しいなら [CodexBar](https://github.com/steipete/CodexBar) を入れたほうがいい。

[SwiftBar](https://github.com/swiftbar/SwiftBar)（xbar でも可）で動くメニューバー用のプラグイン。
Claude Code と Codex に加えて、Antigravity（`agy`）と Grok Build（`grok`）も入っていれば出す。
Windows 版と同じ数字を、同じやり方（それぞれの CLI 自身に聞く）で出す。
認証情報のファイルは読まない・どこにも送らない・モデルは呼ばない。

### 入れ方

1. SwiftBar を入れる：`brew install --cask swiftbar`（GitHub から落としてもよい）。最初にプラグインを置くフォルダを聞かれるので選ぶ
2. そのフォルダに `quotagauge.3m.py` を落として、実行できるようにする（フォルダの場所は自分のものに置き換える）

   ```bash
   curl -fsSL https://raw.githubusercontent.com/kimura-0314/QuotaGauge/main/mac/quotagauge.3m.py -o ~/プラグインのフォルダ/quotagauge.3m.py && chmod +x ~/プラグインのフォルダ/quotagauge.3m.py
   ```

3. SwiftBar のメニューから **Refresh All**。メニューバーに % が出て、押すと中身が開く。3分ごとに更新する（ファイル名の `3m`）

要るもの：macOS の `/usr/bin/python3`（「コマンドラインツールを入れますか」と出たら入れる）と、ログイン済みの `claude`／`codex`（片方だけでもよい）。`agy`・`grok` は見つかった時だけ出る（オンにする操作は要らない）。

### 「見つかりません」と出たら

SwiftBar はシェルの設定を読まないので、Homebrew や nvm で入れたものが見えないことがある。
よくある置き場所（`/opt/homebrew/bin`・`/usr/local/bin`・`~/.local/bin`・`~/.nvm/...`・`~/.volta/bin`・`~/.grok/bin`）は足してあり、それでも見つからない時だけログインシェルの PATH も足す。
「起動できませんでした（node が PATH に無い可能性）」は、本体は見つかったが動かすための node が見つからなかったという意味。
どちらでも、ターミナルで `which claude`・`which codex`・`which node` の結果を教えてほしい。
`agy`・`grok` が出ない時は、`which agy`／`which grok` の場所が上の定番の置き場所にあるかを見る（この2つのためにはログインシェルを聞きに行かない）。

ターミナルで `./quotagauge.3m.py --json` を打つと、読み取った数字だけを出す。

表示言語は macOS の言語設定の1番目に従う。`QUOTAGAUGE_LANG=ja` か `en` で固定できる。
