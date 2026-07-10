# Plan 05 — Performance & Perceived Speed

> Goal: make VibeModel **fast** (real latency) and **feel fast** (perceived speed), and set
> hard requirements the other four plans must honor so they don't regress responsiveness.

> **STATUS (complete).** Combined `/context` endpoint (Win 1), status streaming (Lever 1/2),
> and batch single-wait (Win 2, via Plan 02) all shipped. Win 4 (per-token render O(n²)) fixed
> — `ChatPane` now flushes streamed text on a ~25fps `DispatcherTimer` instead of rebuilding the
> whole string per token. Audited and found **already satisfied**: SettingsDialog blocking (both
> handlers already background their HTTP via `ThreadPool.QueueUserWorkItem`), Plan 01 base64
> encode (runs in the backend off Revit's main thread), and status-line context isolation
> (labels go to the UI stream only, never to `_conversationHistory`). Win 5 (read-only fast path)
> intentionally deferred as marginal.

## TL;DR (honest framing)

The agentic loop makes **up to 25 Anthropic API round-trips per user turn**
(`MaxToolLoopIterations = 25`), each carrying full model latency. That **dwarfs** the
3 local context round-trips (~tens of ms each across the ExternalEvent boundary). So the
genuine *real-latency* wins are **modest** — model latency dominates the wall clock.

The high-leverage work is therefore:

1. **Perceived speed** — status streaming and killing the dead spinner before first token.
2. **Not regressing** when Plan 01's screenshots land (sync `ExportImage` will freeze Revit's UI).
3. A few small real wins: a combined `/context` endpoint and the batch single-wait (Plan 02).

A correction to a common assumption: context is **not** re-fetched every tool iteration. It is
gathered **once per turn** (`AnthropicDirectBackend.cs:121`, `LocalLlmBackend.cs:171`). The cost
is the **3 serial round-trips before the first token**, not per-iteration churn.

---

## 1. Latency map of a typical turn (Anthropic backend)

```
User hits send
  └─ GatherModelContext()              AnthropicDirectBackend.cs:479-533
       /info        (line 492)  ─┐
       /activeview  (line 506)   ├─ 3 SERIAL WebClient.DownloadString calls,
       /selected    (line 517)  ─┘   each crosses the ExternalEvent boundary
  └─ BuildRequestBody + StreamRequest  lines 138-139   ← FIRST token gated behind all 3
  └─ Agentic loop (1..25 iterations):  MaxToolLoopIterations = 25
       each iteration = 1 streamed Anthropic API call
                      + N tool calls, SERIAL, one WebClient each (ExecuteTool 315-349)
```

Per turn:
- **Round-trips to Revit** = 3 (context) + Σ(tool calls across all iterations).
- **Model round-trips** = 1..25 (the loop), each with full streamed-API latency.

`LocalLlmBackend` mirrors this exactly: `GatherModelContext()` at 479-533, tool dispatch at
349-384, `ExecuteTool` at 391-414. `ClaudeCodeBackend` is different — it spawns the `claude`
CLI subprocess (`RunClaudeProcess`, line 352) which runs its own context curls via the Bash
tool, and reuses the session via `--resume` (lines 302-306) so context isn't re-sent. Its cost
is subprocess startup per turn, mitigated by resume.

### ExternalEvent / threading — currently correct, do not break it

- The **HTTP background thread blocks**, not the UI thread:
  `request.ResponseReady.Wait(CommandTimeout)` at `RevitCommandHandler.cs:68`
  (`CommandTimeout = 30s`, line 16).
- The **Revit main thread drains the whole queue** in one `Execute()` and signals each request:
  `while (_queue.TryDequeue(...))` at `RevitCommandHandler.cs:92-115`.
- **The UI does not freeze today.** Multiple queued commands drain in a single `Raise()`.
- One `Raise()` per HTTP request (`RevitHttpServer.cs:276`). Read-only queries (`/info`,
  `/selected`) take the **same heavy ExternalEvent path** as mutations (minor overhead).

---

## 2. Ranked real-perf wins (biggest first)

### Win 1 — Combined `/context` endpoint  *(small effort; best TTFT win)*
Collapse `/info` + `/activeview` + `/selected` into a **single** `/context` call so the backend
makes one round-trip instead of three before streaming. Cuts 2 ExternalEvent crossings at turn
start → directly lowers time-to-first-token.

- New command `ContextCommand : IClaudeCommand` (`Name => "context"`) that internally composes
  the three existing query outputs into one response.
- Backends call it once in `GatherModelContext()` (replace the 3 `DownloadString` calls at
  `AnthropicDirectBackend.cs:492/506/517` and `LocalLlmBackend.cs:492/506/517`).
- Honest note: throughput impact is small vs model latency — this is sold as a **feel** win
  (TTFT), not a throughput win.

### Win 2 — Batch single-wait  *(Plan 02 already covers this — endorse)*
`POST /batch` today does one blocking `EnqueueAndWait` **per line** (`RevitHttpServer.cs:298`),
so a 10-line batch = 10 sequential waits (up to 10 × 30s worst case). Plan 02 restructures this
into a single ExternalEvent wrapped in one `TransactionGroup`. Endorse it and **share the
"enqueue-many / wait-once" plumbing with Win 1**.

### Win 3 — Screenshot freeze (Plan 01) — the real new risk  *(prevent regression)*
`doc.ExportImage(...)` must run on Revit's main thread inside the ExternalEvent, so it **will
freeze the Revit UI for the duration of the export**. This is the heaviest new latency the
project is about to add. Mitigations are hard requirements on Plan 01 (see §4).

### Win 4 — UI string rebuild on long responses  *(minor jank)*
`ChatPane.cs:450-459` reassigns the entire `_streamingTextBlock.Text` on every token →
O(n²) cost on long responses. Append via `Run`/`Inlines` or reflow periodically instead of
rebuilding the whole string per token.

### Win 5 — Read-only fast path  *(marginal; low priority)*
Read-only queries share the heavy ExternalEvent path. The crossing cost is small next to
network/model latency, so a dedicated fast path is not worth the complexity yet.

### Also found — SettingsDialog UI block  *(small, fix while nearby)*
`SettingsDialog.cs:507` and `:515` call `.SendAsync(...).Result` / `.ReadAsStringAsync().Result`
on the UI thread during API-key validation → brief Revit freeze on Save. Move to a background
thread (the `TestConnection` handler already does this correctly via `ThreadPool.QueueUserWorkItem`).

---

## 3. Perceived-speed ("feel fast") levers

### Lever 1 — Status streaming  *(biggest feel win)*
Hook lives in the **backend tool-call loop**, where the command name is known *before* the
blocking HTTP call:
- `AnthropicDirectBackend.cs:280-302` (the `foreach` over `tool_use` blocks) — currently shows
  nothing visible.
- `LocalLlmBackend.cs:370-373` already emits a bare `[toolName]` — upgrade to a friendly verb.

Add a command→verb map (`wall` → "Creating wall…", `screenshot` → "Taking screenshot…",
`context`/`info` → "Looking at the model…") and emit it through the existing `onToken` callback
**before** dispatching `ExecuteTool`.

### Lever 2 — Kill the dead spinner at send
Emit "Looking at the model…" the **instant** the user hits send — *before* `GatherModelContext()`
returns — so the 3-RTT context gather (the main pre-token delay) is never silent. This is the
single biggest first-impression improvement.

### Lever 3 — Status tokens are UI-only
Status lines must **not** be appended to the transcript sent back to the model — they're
presentation only, or they pollute context and confuse the agent.

### Lever 4 — Preserve cancellation
Cancellation already works (Escape + Cancel button: `ChatPane.cs:394-398`, `511-515`, signalling
`_cts.Cancel()` + `_backend.Cancel()`). Keep it. Document that an in-flight `ExportImage` cannot
be cancelled mid-export (it's synchronous on the main thread).

---

## 4. Requirements for plans 01-04

### Plan 01 — Vision / screenshot
- `ExportImage` runs on Revit's main thread inside the ExternalEvent and **WILL freeze the UI**
  for its duration. MUST emit a "Taking screenshot…" status line (Lever 1) **before** the call.
- Keep the **1536px** default long edge.
- Do base64 encoding **off the main thread** — in the backend (`AnthropicDirectBackend`), never
  inside the command.
- Return a **file path** over HTTP, not base64 (already planned — keep it; avoids ~33% bloat).
- Screenshots MUST NOT be added to per-turn context gathering (no auto-screenshot every turn).

### Plan 02 — Transaction integrity
- Endorse the batch restructure (single ExternalEvent + one `TransactionGroup`) — it also removes
  the per-line wait latency (Win 2).
- Build the "enqueue-many / wait-once" pattern **once** and share it with the `/context` endpoint
  (Win 1). Timeout scaling (`max(30s, 5s + 2s×N)`) is sound.

### Plan 03 — I/O contract
- `?format=json` / `Accept: application/json` must **not** add a round-trip. The new `/context`
  endpoint must honor the format flag too.
- `Hint:` lines are free perf-wise — good. Structured-error suggestions should **feed the status
  line** so failures surface a recovery hint to the user, not just the model.

### Plan 04 — Modeling breadth
- Each new command (`grid`, `level`, `view`, `room`, `sheet`, `tag`) must register a friendly
  status verb for the Lever-1 map (e.g. `grid` → "Adding grid…").
- Keep `TransactionHelper` + the modification marker so batch assimilation (Plan 02) works with
  zero special-casing.

### Cross-cutting
The single `/context` endpoint is the shared foundation — all three context-gathering backends
call it once per turn instead of three separate calls.

---

## 5. Edge cases

- **No document open:** `/context` must return a partial result per-section, not fail the turn
  (mirror the existing per-call try/catch at `GatherModelContext()`).
- **Revit busy / modal dialog:** each `/context` sub-section must degrade gracefully; one failing
  section must not blank the others.
- **Status tokens in transcript:** must be filtered out of the model-bound conversation history.
- **Cancellation mid-screenshot:** `ExportImage` is synchronous and non-cancellable once started —
  document this; the Cancel button will only take effect after it returns.
- **Long responses:** the O(n²) `_streamingTextBlock.Text` rebuild (Win 4) gets noticeably janky
  on multi-KB answers.
- **Batch worst-case timeout:** until Plan 02 lands, a long batch can stack per-line 30s waits —
  note in docs.

---

## 6. Verification (with Revit running)

- **Combined endpoint:** `Measure-Command { curl -s http://localhost:18884/context }` vs the
  three serial calls today (`/info`, `/activeview`, `/selected`).
- **Time-to-first-token:** instrument the backend to log timestamps at send → first token →
  complete; compare TTFT before/after `/context` and before/after Lever 2.
- **Screenshot freeze:** during a screenshot command, try to click/drag Revit — observe the freeze
  duration. Wrap `ExportImage` in a `Stopwatch` and log wall time.
- **Batch:** `curl -X POST .../batch` with a 10-line body; time before/after Plan 02.
- **UI jank:** stream a multi-KB response and watch for input lag while it renders.

---

## Appendix — key file:line references

| Concern | Location |
| --- | --- |
| Context gather (Anthropic) | `AnthropicDirectBackend.cs:479-533` (calls at 492/506/517), invoked at 121 |
| Context gather (Local) | `LocalLlmBackend.cs:479-533`, invoked at 171 |
| Tool dispatch (Anthropic) | `AnthropicDirectBackend.cs:280-302`; `ExecuteTool` 315-349 |
| Tool dispatch (Local) | `LocalLlmBackend.cs:349-384`; `ExecuteTool` 391-414 |
| Loop cap | `MaxToolLoopIterations = 25` |
| Claude CLI subprocess | `ClaudeCodeBackend.cs:352`; `--resume` 302-306; idle timeout 379-384/400 |
| Blocking wait (HTTP thread) | `RevitCommandHandler.cs:68`; `CommandTimeout` line 16 |
| Queue drain (main thread) | `RevitCommandHandler.cs:92-115` |
| One Raise per request | `RevitHttpServer.cs:276` |
| Batch per-line wait | `RevitHttpServer.cs:298` |
| Per-token render | `ChatPane.cs:450-459` |
| Status text (connection only) | `ChatPane.cs:124-133`, updated 360-367 |
| Cancellation | `ChatPane.cs:394-398`, `511-515` |
| SettingsDialog UI block | `SettingsDialog.cs:507, 515` |
