# Changelog

## [Unreleased]

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
