# QuotaGauge

Claude Code と Codex の**利用枠の残りをタスクトレイから確認する**Windows用の常駐ツール。

exe 1つで動く。インストーラも .NET ランタイムの追加も要らない。

> **取得したものはすべてローカルに表示するだけ。第三者のサーバーには何も送らない。**
> Claude 側は取得元を2つから選べる（既定は精度優先。詳細は[仕組み](#仕組み)）。

---

## できること

| 操作 | |
|---|---|
| **アイコン** | 一番厳しい枠の使用率をリングで表示。90%以上は赤、70%以上は橙 |
| **左クリック** | Claude と Codex の枠を一覧するパネルが開く |
| **カーソルを乗せる** | それぞれの使用率が出る |
| **右クリック** | 今すぐ更新／**アイコンに出す対象**／ログ／Windows起動時に開始／終了 |

**アイコンがどちらを映すかは選べる。** 右クリック →「アイコンに出す対象」から、
`厳しい方`（既定）／`Claude Code`／`Codex` のどれかを選ぶ。主に使うツールは人によって違うので、
Codex がメインなら Codex だけを映すようにできる（設定は `config.json` に保存される）。

パネルにはこう出る：

```
利用枠

CLAUDE CODE（3分前の値）
  5時間枠                             5%
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  残り 95% ・ あと 4時間31分（8/19 18:59）

  週次                               91%
  ██████████████████████████████████░░
  残り 9% ・ あと 18時間30分（8/20 08:59）

CODEX · PLUS
  週次                               75%
  ███████████████████████████░░░░░░░░░
  残り 25% ・ あと 22時間11分（8/20 12:5x）

最終取得 15:40                    [更新]
```

3分ごとに自動更新する。

## 使い方

1. `QuotaGauge.exe` を好きなフォルダに置いてダブルクリック
2. **アイコンが `^`（隠れているインジケーター）の中にいたら、タスクバーの見える位置へドラッグ**
   （Windows 11 は新しいトレイアイコンを必ず最初にそこへ入れる）
3. 右クリック →「Windows起動時に開始」をON

Codex 側はこれだけで動く。**Claude 側は下のセットアップが要る。**

### Claude Code 側のセットアップ（1回だけ）

Claude Code は利用枠の情報を**ステータスラインのスクリプトにだけ**渡している。
そこで受け取った値をキャッシュに書いてもらう。

`~/.claude/settings.json` の `statusLine` に設定したスクリプトへ、次を足す（Python が使える前提）：

```python
# 受け取った JSON を d としたあと
try:
    import os, time, json
    rl = d.get('rate_limits')
    if rl:
        p = os.path.join(os.path.expanduser('~'), '.claude', 'quota-cache.json')
        tmp = p + '.tmp'
        f = open(tmp, 'w', encoding='utf-8')
        json.dump({'rate_limits': rl, 'updated_at': int(time.time())}, f)
        f.close()
        os.replace(tmp, p)
except Exception:
    pass
```

ステータスラインをまだ使っていない場合は、[公式ドキュメント](https://code.claude.com/docs/en/statusline)を見て設定する。

> ⚠️ **この方式の制約 — 表示される数字は Claude Code 本体と数%ズレることがある**
>
> ステータスラインは**ターミナルで Claude Code を動かしているときにだけ呼ばれる**。
> つまりここに出る値は「**最後にステータスラインが呼ばれた時点**」のもので、常に少し過去のものになる。
> デスクトップアプリだけを使っている間は、まったく更新されない。
>
> そのため、Claude Code 本体の表示が 92% でも、このツールは 91% のまま、といったことが起きる。
> **これは仕様**であって、故障ではない。
>
> 誤解しないように、パネルの見出しには `（3分前の値）` のように**常に鮮度を出している**。
> 5分以上古い場合は見出しの色が変わる。
>
> **Codex 側は毎回 app-server に問い合わせるので常に最新。**

## 仕組み

### Claude Code — 取得元を2つから選べる

右クリック →「**Claude の取得元**」で切り替える。

#### ① Claude Code と同じ経路（**既定**・精度優先）

`~/.claude/.credentials.json` のトークンで `https://api.anthropic.com/api/oauth/usage` に問い合わせる。
Claude Code の画面表示と同じ数字が出る。

- **モデル別の枠まで返る**（`週次（Fable）` のような行）
- **リセット時刻が正確**
- 常に最新

> ⚠️ **これは公開されたインターフェースではない。** Anthropic の公式ドキュメントに記載がなく、
> `anthropic-beta` ヘッダのバージョン値が変われば動かなくなる。
> また [Anthropic Consumer Terms](https://www.anthropic.com/legal/consumer-terms) 第3条は
> 「APIキー経由または明示的に許可された場合を除き、スクリプト等の自動的手段でサービスにアクセスすること」を
> 禁止しており、**この経路がその除外条件を満たすとは読みにくい**。使うかどうかは各自の判断で。

#### ② ステータスライン経由（公開経路のみ）

Claude Code が[公式にステータスラインへ渡している](https://code.claude.com/docs/en/statusline) `rate_limits` を、
スクリプトが書いたキャッシュ（`~/.claude/quota-cache.json`）から読む。**ネットワークアクセスは発生せず、認証情報にも触れない。**

代わりに精度が落ちる：

- **モデル別の枠は含まれない**（5時間枠と週次だけ）
- **5時間枠の `resets_at` が過去を指すことがある**（実測。そのときは残り時間を表示しない）
- ステータスラインが呼ばれた時点の値なので、わずかに遅れる

セットアップは[下記](#claude-code-側のセットアップ1回だけ)。`refreshInterval` を設定すると更新間隔を短くできる。

### Codex

`codex app-server` の JSON-RPC を呼ぶ。

```
initialize
account/rateLimits/read
```

このメソッドは `codex app-server generate-json-schema` が出力する**公式スキーマに定義されている**もの。
レスポンスの `rateLimits.primary` / `secondary` から `usedPercent` / `resetsAt` / `windowDurationMins` を読む。

> Codex CLI に telegram プラグインが入っている環境向けに、app-server を呼ぶときは
> `TELEGRAM_STATE_DIR` を一時ディレクトリへ逃がしている（常駐中のポーラーを止めさせないため）。

## ビルド

```powershell
.\build.ps1
```

Windows 同梱の `csc.exe` を使うので、Visual Studio も .NET SDK も要らない。

> ⚠️ `QuotaGauge.cs` は **UTF-8 BOM付き**で保存すること（`build.ps1` が自動で直す）。

## 動作環境

- Windows 10 / 11
- .NET Framework 4.x（Windowsに標準で入っている）
- Claude 側：Claude Code のステータスライン設定（上記）
- Codex 側：`codex` コマンドが PATH にあること
- 管理者権限は不要

## 免責

このプロジェクトは非公式で、Anthropic および OpenAI とは関係がありません。

表示される値は各ツールが提供する情報をそのまま出しているだけで、正確性・即時性は保証しません。
請求や契約上の根拠として使わないでください。

インターフェースや値の形式は予告なく変わることがあります。

## ライセンス

[MIT](LICENSE)
