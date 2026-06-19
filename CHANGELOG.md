# Changelog

## [Unreleased]

### Added
- **Structured I/O (foundation)** — commands can return JSON via `?format=json` or an explicit
  `Accept: application/json` header (curl's default `*/*` stays text). Success envelope
  `{"ok":true,"data":{...}}` (or `{"ok":true,"text":"..."}` for unmigrated commands); failure
  `{"ok":false,"error":{"code","message","suggestion"}}`. Text mode is byte-identical except
  errors gain an additive `Hint:` line. New `CommandResult` type + optional `IStructuredCommand`
  interface (per-command migration; `info` migrated first). Registry now runs every command
  through one `ExecuteCore` returning a structured result; the batch path decides Assimilate vs
  RollBack on `.Success` instead of string-sniffing `"ERROR"`. Batch JSON output and the
  remaining query-command migrations are a follow-up.
- **Vision** — new `/screenshot` command exports the active view to a PNG (default 1536px long
  edge, `screenshot [pixels]`) and returns its path. `AnthropicDirectBackend` reads the file and
  inlines it as an image block so the model can SEE the model; both Anthropic and Claude-CLI
  backends are nudged to screenshot **autonomously** (after visible changes / layout questions,
  not for pure data queries). Only the most recent screenshot is kept as an image in history
  (older ones downgrade to text) to avoid re-shipping base64 every turn; old temp PNGs are pruned
  to the last 20. Local LLM backend gets the path as text only (most local models aren't vision-capable).
- `POST /batch` now runs as **one undo unit** — the whole batch wraps in a single Revit
  `TransactionGroup` and collapses to one Ctrl+Z (undo label e.g. `"VibeModel: 4× wall"`).
  Resilient by default (a mid-batch failure keeps the commands that succeeded and reports the
  per-command `ERROR`); opt-in atomic mode (`#atomic` first line or `?atomic=1`) rolls back the
  entire batch on any failure. Read-only batches create no undo entry.
- Combined `/context` endpoint composing `info` + `activeview` + `selected` in one call.
- Friendly status lines streamed during chat tool calls (`StatusVerbs`) — "Creating wall…",
  "Looking at the model…", etc.

### Changed
- Chat backends gather model context via a single `/context` round-trip instead of three serial
  HTTP calls, each of which crossed the ExternalEvent boundary. Measured ~2–3× faster
  steady-state and ~2.2s→95ms on the cold first turn of a session (lower time-to-first-token).
- Batch is now handled as a single main-thread request (`EnqueueBatchAndWait` → `ExecuteBatch`)
  rather than a blocking `EnqueueAndWait` per line, with a batch-size-scaled timeout
  (`max(30s, 5s + 2s×N)`, capped at 5 min) and a matching socket send-timeout.
- Chat tool-activity labels now appear **before** the tool runs (was after) and use a friendly
  verb instead of the raw `[toolName]`; the streaming placeholder shows "Looking at the model…"
  the instant a message is sent. Status lines are UI-only — never added to conversation history.

## [v1.1.4] — Chat Session Continuity (2026-05-05)

### Fixed
- Chat session no longer goes amnesiac when the Claude CLI subprocess is killed mid-task — `ClaudeCodeBackend.cs` now captures `session_id` from the first `system` init NDJSON event instead of waiting for the terminal `result` event, so a follow-up like "ja?" continues the prior conversation via `--resume` rather than starting fresh
- Replaced 2-min absolute process timeout with a 3-min idle timeout that resets on every NDJSON line — multi-step modeling tasks with continuous tool calls are no longer cut off prematurely
- Idle kills now produce a visible chat-bubble notice (via both `onToken` and `fullResponse`) instead of silent termination; mid-tool kills also clear `_sessionId` to prevent broken `--resume` transcripts (orphan `tool_use` without matching `tool_result`)
- Stale `--resume` failures (e.g., CLI session store rotated) auto-retry once as a fresh chat with a `[Resumed session was stale — starting fresh chat.]` prefix instead of silently dying

### Changed
- Clarified `assistant`-block iteration in `ProcessNdjsonLine` by separating `currentBlockType` (per-iteration) from `lastBlockType` (post-loop), removing a dual-use variable that could reuse a stale type when a block lacked a `type` field

## [v1.1.3] — Upgrade Recommended Model to Qwen 3.5 9B (2026-03-27)

### Changed
- Recommended local LLM model updated from Qwen 2.5 7B to Qwen 3.5 9B across docs and UI
- Model table in `LOCAL_LLM_SETUP.md` now lists Qwen 3.5 family (0.8B, 4B, 9B)
- Hardware requirements table updated for new model sizes
- Settings dialog tool-use hint updated to reference Qwen 3.5 9B+

## v1.1.2 — Fix Local LLM Empty Response (2026-03-24)

### Fixed
- Local LLM backend returned empty responses — `JavaScriptSerializer` deserializes JSON arrays as `ArrayList`, not `object[]`, causing silent cast failures in stream parsing
- Same latent bug fixed in Claude Code backend (`ClaudeCodeBackend.cs`) and Settings "Test Connection" (`SettingsDialog.cs`)

## v1.1.1 — Fix DLL Blocked by Windows (2026-03-23)

### Fixed
- Installer now automatically unblocks DLLs downloaded from the internet (`Unblock-File`) — prevents "External Tool Failure" / HRESULT: 0x80131515 on first load
- Added DLL unblock step to manual installation instructions in README
- Added troubleshooting entry for FileLoadException / blocked DLL error

## v1.1.0 — Local LLM Support (2026-03-21)

### Added
- Local LLM backend (`LocalLlmBackend`) — connects to any OpenAI-compatible server (llama.cpp, Ollama, LM Studio)
- Shared `ToolDefinitionBuilder` for generating tool definitions in both Anthropic and OpenAI formats
- Settings UI redesigned with tabbed backend selector (Claude CLI / Anthropic API / Local LLM)
- Local LLM settings: endpoint URL, model name, tool use toggle, response timeout slider (30-300s)
- Test Connection button for validating local LLM server reachability
- Chat-only info banner when local LLM is connected without tool use enabled
- Backend status indicator in chat header showing active backend type
- Backend hot-switch via Settings with automatic session reset
- Setup guide (`docs/LOCAL_LLM_SETUP.md`) covering llama.cpp, Ollama, LM Studio, recommended models, and troubleshooting

### Changed
- Backend selection logic now supports `auto`, `claude-cli`, `anthropic-api`, and `local-llm` modes
- `AnthropicDirectBackend` refactored to use shared `ToolDefinitionBuilder`
- Settings dialog now saves all backend settings on save (not just active tab)
- Default preferred backend changed from `direct` to `auto`
- Welcome message updated to mention local LLM option
