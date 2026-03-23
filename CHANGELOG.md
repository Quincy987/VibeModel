# Changelog

## [Unreleased]

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
