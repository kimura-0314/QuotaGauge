# QuotaGauge

Claude Code と Codex の**利用枠の残りをタスクトレイから確認する**Windows用の常駐ツール。

exe 1つで動く。インストーラも .NET ランタイムの追加も要らない。

> **認証情報には一切触れない。外部への通信もしない。**
> どちらも各ツールが公式に提供している経路だけを使う（詳細は[仕組み](#仕組み)）。

---

## できること

| 操作 | |
|---|---|
| **アイコン** | 一番厳しい枠の使用率をリングで表示。90%以上は赤、70%以上は橙 |
| **左クリック** | Claude と Codex の枠を一覧するパネルが開く |
| **カーソルを乗せる** | それぞれの使用率が出る |
| **右クリック** | 今すぐ更新／ログ／Windows起動時に開始／終了 |

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

> ⚠️ **この方式の制約**
> ステータスラインは**ターミナルで Claude Code を動かしているときにだけ呼ばれる**。
> デスクトップアプリだけを使っている間、Claude 側の値は更新されない。
> パネルには `（N分前の値）` と鮮度が出るので、古い値を最新と誤解することはない。
> **Codex 側は常に最新**（毎回 app-server に問い合わせる）。

## 仕組み

**どちらも公式に用意されている経路しか使わない。** 認証トークンを読むことも、非公開のエンドポイントを叩くこともしない。

### Claude Code

ステータスラインへ渡される JSON の `rate_limits` を使う。これは[公式ドキュメントに記載されたフィールド](https://code.claude.com/docs/en/statusline)：

- `rate_limits.five_hour.used_percentage` / `resets_at`
- `rate_limits.seven_day.used_percentage` / `resets_at`

本ツールはそれを書き出したキャッシュ（`~/.claude/quota-cache.json`）を読むだけ。**ネットワークアクセスは発生しない。**

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
