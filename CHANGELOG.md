# Changelog

## 2.3.0

### Added

- **Optional providers: Antigravity CLI (`agy`) and Grok Build (`grok`).** Off by default; turn
  them on from the right-click menu ("Also watch") or with `"agy": "on"` / `"grok": "on"` in
  `config.json`. Both are read the way Claude Code and Codex already are — by asking the CLI itself
  (`agy --print "/quota"` in print mode, and the `_x.ai/billing` ACP extension over
  `grok agent stdio`), so no credentials are read and nothing goes over the network from this app.
  The tray icon can follow either of them.
- **Codex: Luna Reserve and reset credits.** The same `account/rateLimits/read` call also carries
  the per-limit quotas (`gpt-reserve`, shown under its documented name Luna Reserve) and
  `rateLimitResetCredits`, so the Codex heading now says how many manual resets you have left.
- `QuotaGauge.exe --once` fetches every provider one time, writes the result to `last-fetch.txt`
  and exits. Useful when a provider shows an error and you want to see the raw outcome.
  `--preview` opens the panel after the first fetch and saves it to `panel-preview.png`.

### Changed

- **The panel is drawn as rings.** Each window was a heading, a bold percentage, a bar and a
  sentence repeating the same numbers; with four CLIs that read as a wall of text. Each window is
  now the same ring the tray icon draws, three to a row, coloured per CLI, with the reset shown as
  date and time underneath. Colours follow the Google Power Tools side-panel tokens.
- **Rendered at the real DPI.** The process declares per-monitor DPI awareness, so on a 125% or
  150% display Windows no longer stretches a 96-dpi bitmap and the panel is sharp.
- Reset times are rounded to the minute. The server occasionally returns `07:59:59.9Z` for a
  window that resets at 08:00, which used to display as 16:59 where Claude Code shows 17:00.
- Providers are now fetched in parallel, so a refresh takes as long as the slowest one instead of
  the sum.

## 2.2.0

### Added

- **The interface is available in English.** It follows your Windows display language: Japanese on
  a Japanese system, English everywhere else. Set `"language"` in `config.json` to `ja` or `en` to
  pin it. Until now everything the app showed — the panel, the tray menu, every error message —
  was Japanese only, which made the English README a promise the app did not keep.

  The two languages are held side by side at each call site rather than in a separate table, so one
  cannot quietly fall behind the other.

### Changed

- The assembly title and description are now the product name and an English sentence, so the exe
  identifies itself the same way in any locale.

## 2.1.1

### Fixed

- **Clicking the tray icon while the panel was open reopened it instead of closing it.** The click
  moves focus away first, so `Deactivate` had already hidden the panel by the time the click
  handler ran — it saw a hidden panel and showed it again. The panel now remembers when it was
  hidden, and a click arriving right after that is treated as the close it was meant to be.
  Until now the only way to dismiss the panel was to click somewhere else.
- `tools/make-icon.ps1` is saved as UTF-8 with a BOM. Without it Windows PowerShell 5.1 reads the
  file as ANSI and its Japanese output comes out as mojibake.

## 2.1.0 — first public release

Everything before this lived only on my machine, so the history below starts here.

### Changed

- **Claude usage now comes from Claude Code itself.** QuotaGauge starts `claude` in stream-json
  mode and sends a single `{"subtype":"get_usage"}` control request, mirroring how the Codex side
  already talks to `codex app-server`. It no longer reads `~/.claude/.credentials.json` or calls
  `api.anthropic.com` on its own.
- **Per-model weekly windows and the plan name are now shown.** The panel heading reads
  `Claude Code · max`, and model-scoped windows appear as their own rows.
- **The first-run dialog is gone.** It existed to make you choose between the old data sources;
  that choice is no longer needed. Both older routes remain in the right-click menu.
- Startup of the Claude fetch takes 7–10 seconds. Empty MCP and hook configs are passed to the
  child process, which brought this down from 16.6s. No model is invoked, so nothing is billed.

### Fixed

- **An expired token no longer stalls the app.** Claude Code refreshes OAuth before fetching. The
  previous default had no way to refresh and hammered the endpoint until it returned 429.
- Repeated failures on the direct-endpoint route now back off (3min → 6 → 12 → … → 60) instead of
  retrying every three minutes. "Refresh now" ignores the wait.
- Overlapping fetches are no longer started, and the Codex read is capped at 15 seconds — together
  these stop worker threads and `codex` processes from piling up when a call hangs.
- The log path handed to Notepad is quoted, so "view log" works when the path contains a space.
- `config.json` and `quotagauge.log` fall back to `%LOCALAPPDATA%\QuotaGauge\` when the folder
  holding the exe is not writable. They used to fail silently.
- The same error is no longer appended to the log every three minutes; only changes are written.

### Security

- The working directory of the `claude` and `codex` child processes is pinned. Without it the
  folder holding the exe became the current directory, so a `claude.exe` or `codex.cmd` dropped
  beside it could be picked up by the executable search order.

### Added

- An application icon, generated by `tools/make-icon.ps1` from the same drawing code the tray icon
  uses, so the two cannot drift apart.
- An English README alongside the Japanese one.
