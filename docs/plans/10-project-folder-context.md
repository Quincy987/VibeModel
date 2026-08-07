# Plan 10 — Project Folder Context

> Goal: let the user point VibeModel's chat at a folder of reference material — briefs,
> specs, schedules, consultant PDFs, survey notes — and have every chat backend treat that
> folder as ambient project knowledge, without pasting files into the conversation one at a
> time. Attachments (merged from t-177) answer "look at THIS file"; this plan answers "you
> can always look in THERE".

> **STATUS (proposed).** Nothing here is built. Builds directly on the attachment feature
> (`AttachmentStore`, `AttachmentFormatting`, the `ChatAttachment` classifier) and the
> existing command registry; independent of plans 08/09 (memory), which store distilled
> facts, not documents.

## TL;DR

One per-project setting: a folder path. Once set,

1. every chat turn's system prompt carries a **manifest** of that folder (file names, kinds,
   sizes — cheap, bounded), so the model knows what reference material exists;
2. two new registry commands, **`docs`** (list the folder) and **`readdoc <name>`** (return
   one file's content), let any consumer — in-app chat backends via tool use, Claude Code
   via curl, a human via browser — pull a document on demand;
3. the Claude Code backend additionally gets the folder via `--add-dir`, so it can read the
   files natively with its own tools.

Nothing is ever inlined eagerly. The manifest is the only always-on cost; content moves only
when the model asks for a specific file. The folder is **read-only** to VibeModel.

---

## 1. The setting — one folder per Revit project

`SettingsManager` is a flat global dictionary (`%LOCALAPPDATA%\VibeModel\settings.json`), so
the per-project association is a keyed map inside it:

```json
{ "ProjectContextFolders": { "Hospital_North_Wing": "C:\\Projects\\HNW\\docs" } }
```

- Key = the same sanitized project key the attachment store already uses
  (`AttachmentStore.SanitizeName` / `ChatPane.GetProjectKey()`), so attachments and folder
  context agree on project identity for free.
- New accessors: `GetProjectContextFolder(projectKey)` / `SetProjectContextFolder(projectKey, path)`.
  Getter returns null if the stored path no longer exists (folder moved/renamed) — callers
  degrade to "no folder configured" instead of erroring every turn.
- UI: a "Project folder" row in `SettingsDialog` — read-only path textbox + Browse
  (folder picker) + Clear. The dialog already knows the active document, so it edits the
  entry for the current project only.
- No chat-command setter in phase 1. Picking a folder is a trust decision; keep it on a
  deliberate UI surface, not something the model can be talked into changing.

## 2. `docs` and `readdoc` — folder access as ordinary commands

Both are plain `IClaudeCommand`s in `Services/Claude/Commands/`, auto-discovered like every
other command. That one choice gives us all transports at once: the HTTP API (curl from
Claude Code or a terminal), the in-app backends (commands are already exposed to the model
as tools via `ToolDefinitionBuilder`), and `/batch`.

**`docs`** — manifest of the configured folder for the active project:

```
Project folder: C:\Projects\HNW\docs  (12 files, 2 subfolders)
  brief-rev-C.pdf          [pdf]   1.2 MB
  room-schedule.csv        [text]  84 KB
  survey/site-levels.txt   [text]  12 KB
  ...
Use: readdoc <name>
```

Recurses one level deep (phase 1), relative paths shown. Kind tags reuse
`ChatAttachment.ClassifyExtension`. If no folder is configured, it says so and names the
Settings dialog — same "tell them how to fix it" convention as the error hints.

**`readdoc <relative-name>`** — returns one file:

- `[text]` kinds (txt/md/csv/json/xml/log): full content, capped at
  `AttachmentFormatting.MaxInlineTextChars` (400k chars) with the same truncation notice.
- `[pdf]` / `[image]`: phase 1 returns the absolute path plus a hint — Claude Code reads the
  path natively; for the API backends see §3 phase 2.
- Name resolution is case-insensitive on the relative path; ambiguous prefixes list matches.

**Safety invariants (both commands):**

- Resolve against the configured root and reject anything that escapes it
  (`Path.GetFullPath` + the same separator-suffixed prefix check `AttachmentStore.StoreFile`
  uses — that boundary logic gets extracted to a shared helper rather than copied).
- Read-only: no command in this plan writes, moves, or deletes inside the folder.
- Symlinks/junctions that point outside the root fail the resolved-path check and are
  rejected with a clear error.
- Manifest bounded: max 200 entries listed; beyond that, count + "filter by subfolder".

## 3. Injection — what each backend does with the folder

**System prompt (all backends).** When a folder is configured, `ChatBackendBase` appends a
short block to the per-turn context: the folder path, the `docs` manifest (bounded, §2), and
one instruction — "these are the project's reference documents; use `readdoc <name>` when one
is relevant; don't guess at their contents." Manifest generation is shared with the `docs`
command so the two can never drift.

**Claude Code backend.** Adds `--add-dir <folder>` to the CLI invocation (the invocation
already assembles args + `--append-system-prompt` in `ClaudeCodeBackend`), and the appended
prompt names the folder. The CLI then reads/greps files with its own tools — no `readdoc`
round-trip needed, though it still works via curl.

**API backends (Anthropic direct, local LLM with tool use).** `docs`/`readdoc` arrive as
tools automatically through the existing command-tool bridge; the system-prompt manifest
tells the model they're worth calling. Local LLM without tool use gets manifest-only — the
model can at least tell the user what exists and ask them to attach a file.

**Phase 2 — rich files for the API backend.** `readdoc` on a `[pdf]`/`[image]` returns a
marker the Anthropic backend intercepts and converts into a native document/image content
block on the next request (reusing the attachment upload path from t-177, including
`ImageResizer` caps). This is the only backend-specific special case in the plan, and it is
additive.

## 4. Phasing

- **Phase 1 (the feature):** setting + accessors, SettingsDialog row, `docs`, `readdoc`
  (text inline; pdf/image as path), system-prompt manifest, `--add-dir` for Claude Code.
- **Phase 2:** native pdf/image delivery to the Anthropic backend; manifest caching with a
  directory watcher if per-turn enumeration ever shows up in latency traces (do not
  pre-build this — a one-level listing of a docs folder is microseconds).

## 5. Files touched

- `src/VibeModel/Infrastructure/SettingsManager.cs` — folder get/set (keyed map).
- `src/VibeModel/UI/SettingsDialog.cs` — Project folder row (browse/clear).
- `src/VibeModel/Services/Claude/Commands/DocsCommand.cs` — new.
- `src/VibeModel/Services/Claude/Commands/ReadDocCommand.cs` — new.
- `src/VibeModel/Services/Chat/ProjectDocs.cs` — new: shared manifest builder + root
  boundary check (extracted from `AttachmentStore`).
- `src/VibeModel/Services/Chat/ChatBackendBase.cs` — manifest block in per-turn context.
- `src/VibeModel/Services/Chat/ClaudeCodeBackend.cs` — `--add-dir`.
- `src/VibeModel.Tests/ProjectDocsTests.cs` — new (see §6).

## 6. Risks & test posture

- **Path escape is the real risk.** Tests: `..` traversal, absolute-path argument, prefix
  sibling (`docs-x` vs `docs`), case tricks — all must be rejected. The boundary helper is
  pure and fully unit-testable headlessly.
- **Context bloat.** The manifest is names-only and capped; `readdoc` reuses the existing
  400k-char truncation. Test: giant CSV truncates with notice, manifest caps at 200.
- **Stale folder.** Deleted/renamed folder degrades to "not configured" (tested via the
  accessor), never a per-turn error loop.
- **Trust surface.** The folder is user-chosen in a dialog, read-only, and never written by
  the model. `readdoc` output is data, not instructions — the standard prompt-injection
  caveat for any document the user points an LLM at; no new exposure beyond what
  attachments already accept.
- All new logic (manifest, boundary, name resolution, truncation) is Revit-free and runs in
  the headless suite (`dotnet test VibeModel.sln -p:RevitVersion=2023`).
