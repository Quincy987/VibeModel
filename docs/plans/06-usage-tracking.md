# Plan 06 — Usage Tracking & Export

> Goal: give the developer/admin visibility into how users actually use VibeModel —
> which prompts they write, which patterns repeat, which tools dominate, what it costs —
> by **durably** recording usage locally per user, and letting each user export a single
> file to send back. VibeModel is not hosted; every user runs it locally, so telemetry has
> to be file-based and self-shipped, not phoned home.

> **STATUS (proposed).** Nothing in this plan is built yet. This supersedes the naive
> "dump the chat panel to a file" idea — see §1 for why that idea alone yields almost nothing.

## TL;DR

The button the user pictured ("Download usage data") is the **last 5%**. The real work is a
**persistent, always-on usage log** written as usage happens — because the only thing that
records chat today (`ChatHistory`) is an **in-memory 100-message ring buffer** that is wiped on
every Revit close and every "New Chat" (`ChatHistory.cs:15`, `ChatPane.cs:258,564`). A button
that dumps it captures a fraction of one session at best.

Two facts shape the design:

1. **No token/cost data exists today.** Neither `AnthropicDirectBackend` nor `LocalLlmBackend`
   parses the `usage` block from the API response — grep for `input_tokens` finds nothing. Cost
   analytics is net-new parsing (phase 2), not a formatting change.
2. **One universal choke point already sees every tool call.** Every command — from the in-app
   Anthropic/Local backends, from the Claude CLI child process, *and* from external Claude Code
   sessions over curl — funnels through `RevitCommandHandler.Execute` (`RevitCommandHandler.cs:146`)
   on the Revit main thread, already inside a try/catch. Log there and you capture **all** tool
   usage universally, in one place, with success + timing for free.

Recommended shape: a new **`UsageTracker`** (a sibling to `Logger`/`ChatHistory`) that appends
**JSONL** events to `%LOCALAPPDATA%\VibeModel\usage\`, fed by two capture points — command events
(server dispatch) and turn events (chat panel) — plus an **Export** button that zips the folder
with a computed summary. Always-on, with a visible opt-out and a "clear data" control for consent.

---

## 1. Why "just dump the chat panel" fails

`ChatHistory` is the tempting source, but:

- **Ephemeral.** `List<ChatHistoryEntry>` in memory, capped at `MaxEntries = 100`
  (`ChatHistory.cs:14-15`). Cleared in `InitializeBackend` and `OnNewChatClick`
  (`ChatPane.cs:258,564`) and gone entirely when Revit exits. A user who chats for a week and
  clicks the button has, at most, the tail of their current session.
- **No tool detail.** It stores only `("You"|"Claude"|"System", text)`. Which Revit tools ran,
  how many iterations, whether they succeeded — none of it is there. Tool calls happen *inside*
  the backend agentic loop, invisible to `ChatHistory`.
- **No cost/timing.** No tokens, no durations, no model, no backend.

So the export target must be a **new durable log written as events occur**, not a snapshot of a
volatile buffer.

---

## 2. What to capture

Two event streams, both keyed by a per-launch **session id** (a GUID minted at add-in startup)
and a stable **anonymous machine id** (a salted hash of machine name + user, so the admin can
dedupe and count distinct users across files without learning who they are).

### 2a. Command events — the universal stream (from `RevitCommandHandler`)

One event per executed command / batch item. Captures the "most tools" and a lot of the
"patterns" signal, for **every** caller including external Claude Code.

```json
{ "t": "cmd", "ts": "2026-07-11T14:03:22Z", "sid": "…", "mid": "…",
  "name": "wall", "argsPreview": "0 0 5000 0 3000", "ok": true, "ms": 12, "mod": true }
```

- `name` — command name (e.g. `wall`, `list`, `info`, `exec`).
- `argsPreview` — args, **truncated** (e.g. 120 chars) and redaction-filtered (§5). Enough to see
  intent, not a full data dump.
- `ok` — success (`request.Result` didn't start with `ERROR:` / structured `ok=false`).
- `ms` — main-thread execution time (cheap `Stopwatch` around the dispatch).
- `mod` — was it a modification (via `_registry.IsModification`), so read/write ratio is analyzable.
- Batch → emit one `cmd` event per item plus a `batch` summary event (count, atomic, rolledBack).

### 2b. Turn events — the conversational stream (from `ChatPane`)

One event per user turn in the in-app chat. This is the **only** place natural-language prompts
exist (they go to the LLM, never to the server), so it's essential for "what prompts they use"
and "repeated patterns".

```json
{ "t": "turn", "ts": "2026-07-11T14:03:20Z", "sid": "…", "mid": "…",
  "backend": "anthropic", "model": "claude-sonnet-4-5",
  "prompt": "make a 5x5m room", "promptChars": 15,
  "responseChars": 412, "toolCalls": ["info","wall","wall","wall","wall"],
  "iterations": 3, "ms": 4200, "outcome": "ok",
  "inTokens": null, "outTokens": null }
```

- `prompt` — the user's text. This is the highest-value field for the admin *and* the most
  privacy-sensitive (§5). Kept full by default; redaction + opt-out govern it.
- `backend` / `model` — from the active backend + `SettingsManager.GetModel()`.
- `toolCalls` — ordered tool names for the turn. For the API backends, captured via
  `ChatBackendBase.ExecuteTool` attributing each call to the active turn. For the **Claude CLI**
  backend, tools run out-of-process so the turn won't list them — but the §2a `cmd` stream still
  captures them (correlate by `sid` + timestamp).
- `outcome` — `ok` / `error` / `cancelled` (straight from the existing `onComplete`/`onError`
  callbacks).
- `inTokens`/`outTokens` — `null` until phase 2 (§6) wires up usage parsing.

Correlating the two streams by `sid` + time gives the full picture; neither alone is enough.

---

## 3. Storage

- **Location:** `%LOCALAPPDATA%\VibeModel\usage\` — the folder already holds `settings.json`
  and `logs/`, so users/admins know where to look.
- **Format:** append-only **JSONL**, one self-contained event per line. Robust to crashes
  (a torn last line costs one event), trivial to append, trivial for the admin to load into
  anything (jq, pandas, Excel-via-script).
- **Rotation:** monthly file `usage-YYYY-MM.jsonl`. Keeps any single file bounded; export grabs
  them all.
- **Always-on** (unlike `Logger`, which is gated behind `VIBEMODEL_DEBUG`) — the whole point is
  to accumulate history in the background. Gated instead by the §5 opt-out setting.
- **Never throws, never blocks Revit.** Mirror `Logger`'s swallow-all discipline. Because §2a
  runs on the **Revit main thread**, the tracker must not do synchronous disk I/O inline — use a
  `ConcurrentQueue` + a single background writer thread (or `BlockingCollection`) that drains and
  appends. Per the project memory that *ExternalEvent latency dominates*, we add **zero** blocking
  work to the command path — just an enqueue.

---

## 4. Export & summary (the "button")

- **Home: the Settings dialog**, not the chat header. The header (`ChatPane.CreateHeader`) has
  room for ~2 buttons and is per-turn UI; export is an occasional admin action. Add an **"Export
  Usage Data…"** button to `SettingsDialog` alongside a short line explaining what it does. (A
  header button is possible if the user insists on one-click, noted as an alternative.)
- **Action:** zip everything under `usage/` (via `System.IO.Compression.ZipFile`) to a path the
  user picks (default: Desktop), named `VibeModel-usage-<mid>-<date>.zip`. Show a confirmation
  with the full path so the user can find and attach it.
- **Include a computed `summary.txt`** in the zip (and show it in a dialog): date range, turn
  count, distinct sessions, top 10 tools, read/write ratio, error rate, and — once phase 2 lands
  — total tokens and estimated cost. This makes the export meaningful to the user (transparency)
  and gives the admin an instant read before diving into the raw JSONL.
- **Optional `/usage` HTTP command** returning the same aggregated stats as JSON — lets the admin
  script collection and lets a future in-app "usage" view reuse the same aggregator.

---

## 5. Privacy & consent (must-have, not optional)

Recording prompt text locally and asking users to send it is reasonable **only with disclosure
and control**:

- **Visible toggle** in Settings: "Record usage data locally (for sharing with the developer)",
  with a one-line explanation that it stays on disk until the user chooses to export and send it.
  New `SettingsManager` getter/setter (`GetUsageTrackingEnabled`, **default on** — decided).
  Tracking runs from first launch; the disclosure line in Settings makes it visible and the toggle
  lets any user opt out. Rationale: the admin distributes to known users and needs history to
  accumulate without each user having to enable it.
- **"Clear usage data"** button next to Export — deletes the `usage/` folder.
- **Redaction pass** before writing `prompt` and `argsPreview`: strip obvious secrets
  (API-key-shaped tokens, anything matching the `VIBEMODEL_TOKEN` value, long hex/base64 blobs)
  and truncate. Document that `exec` command bodies and prompts may contain project-identifying
  text.
- **Document it** in `CLAUDE.md` / README so it's not a surprise.

---

## 6. Phasing

**Phase 1 — durable capture + export (the core).**
- `UsageTracker` (queue + background JSONL writer, session/machine id, redaction, rotation).
- Instrument `RevitCommandHandler.Execute` (cmd/batch events) and `ChatPane.SendCurrentMessage`
  (turn events); `ChatBackendBase.ExecuteTool` attributes tools to the active turn.
- `SettingsManager` toggle; `SettingsDialog` Export + Clear buttons + disclosure; zip + summary.
- Delivers everything the user asked for: prompts, patterns, most-used tools — durably.

**Phase 2 — cost/token analytics (net-new parsing).**
- Parse the `usage` block: Anthropic streaming `message_delta` carries `output_tokens` and the
  initial `message_start` carries `input_tokens`; the Local OpenAI-compatible response carries a
  `usage` object. Fill `inTokens`/`outTokens` on turn events. Add token/cost rollups to the
  summary. Touches the SSE/NDJSON parse in each backend, so it's isolated to phase 2.

**Phase 3 (optional) — admin tooling.**
- A small companion `analyze_usage.py` (top tools, prompt clustering, turns/day, error rate,
  cost/day) the admin runs over collected zips. Pure offline, no add-in changes.

---

## 7. Files touched

**New**
- `src/VibeModel/Infrastructure/UsageTracker.cs` — the logger + background writer + ids + redaction.
- `src/VibeModel/Services/Claude/Commands/UsageCommand.cs` — optional `/usage` aggregate (phase 1/2).
- `src/VibeModel.Tests/UsageTrackerTests.cs` — serialization, rotation, redaction, summary aggregation.
- (phase 3) `scripts/analyze_usage.py`.

**Modified**
- `RevitCommandHandler.cs` — wrap dispatch in a `Stopwatch`, emit cmd/batch events.
- `ChatPane.cs` — start a turn on send; finalize on complete/error/cancel with outcome + timing.
- `ChatBackendBase.cs` (`ExecuteTool`) — record each tool into the active turn.
- `SettingsManager.cs` — usage-tracking toggle getter/setter.
- `SettingsDialog.cs` — Export + Clear buttons, disclosure text, wire to `UsageTracker`.
- `VibeModelApp.cs` — mint the session id at startup; flush/stop the writer on shutdown.
- `CLAUDE.md` / README — document the feature, storage path, and privacy posture.

## 8. Risk & test posture

- **Low-to-moderate.** Capture is additive and off the hot path (enqueue only on the main thread;
  disk I/O on a background writer). The tracker must never throw and never block — same contract
  as `Logger`.
- **Headlessly testable.** `UsageTracker` serialization, monthly rotation, redaction, and the
  summary aggregator are pure and unit-testable in the existing xUnit suite (no Revit needed).
  The zip/export and Settings wiring get a manual verify pass.
- **Fits the workflow.** Phase 1 is a self-contained build → review → verify unit; phase 2 layers
  on without reworking phase 1.
