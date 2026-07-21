# Plan 08 — Profiles & Memory

> Goal: VibeModel learns. A per-user **profile** (how this user words things, their habits,
> their corrections) plus a **global capability map** (what turned out to be impossible in
> Revit/VibeModel and what worked instead), captured during chat, injected into every future
> turn, and stored as plain shareable files so curated global facts can later ship with the
> repo for new users (seed pack — separate plan 09).

> **STATUS (proposed).** Nothing here is built. Note: plan 06 (usage tracking/export) was
> **never built** — no export pipeline exists, so this plan ships memory as plain local files
> and leaves zip-export bundling to plan 06 / plan 09 whenever they land.

## TL;DR

Two memory scopes, one store, two capture paths, two injection points:

- **Scopes, never mixed.** `user` = personal profile (corrections, phrasing, modeling habits,
  preferred families/units). `global` = impersonal capability facts ("`place` fails for hosted
  families without a host → select the host wall and use `placeid`"). Global is the only
  shareable slice; the split is the privacy boundary.
- **Store.** `%LOCALAPPDATA%\VibeModel\memory\` — `profile.jsonl` + `global.jsonl`, append-only
  JSONL with tombstones, managed by a new `MemoryStore` (sibling of `Logger`/`SettingsManager`,
  same never-throw discipline).
- **Capture path 1 (phase 1, the reliable one): the LLM decides.** A new auto-discovered
  `remember` command. When the user corrects the model, it saves a `user` fact; when it hits a
  dead end and finds a working alternative, it saves a `global` fact. Reflection discovery via
  `ClaudeCommandRegistry` means the one class is simultaneously an in-app chat tool for the
  Anthropic/Local backends AND a curl endpoint (`/remember`) for external Claude Code sessions —
  no extra wiring. Letting the model judge "worth keeping" beats regex-detecting corrections.
- **Capture path 2 (phase 2, automatic): failure→workaround pairs.** Every command already
  returns a structured ok/error envelope. Inside one chat turn, `ok:false` for command A
  followed by success of a different command B at the same intent is a global-map **candidate**
  — logged automatically as unverified, promoted only by review (§2b). Candidates are never
  injected into prompts.
- **Injection, fresh every turn on all three backends.** The Anthropic + Local backends share
  `ChatBackendBase.BuildSystemPrompt()` (`ChatBackendBase.cs:189`), rebuilt on every send
  (`AnthropicDirectBackend.cs:387`, `LocalLlmBackend.cs:359`). The Claude CLI backend rewrites
  its prompt file before **every** message (`ClaudeCodeBackend.cs:148-150`,
  `BuildSystemPromptText` at `:165`). So a fact saved this turn is live on the next turn
  everywhere, with no cache-invalidation machinery.

---

## 1. Entry format

One JSON object per line, both files share the shape:

```json
{ "id": "a1b2c3d4", "ts": "2026-07-21T10:00:00Z",
  "text": "User draws walls centreline-justified; never assume finish-face",
  "why": "corrected me after I placed walls finish-face on 21 Jul",
  "source": "user-correction", "verified": true, "revit": null, "deleted": false }
```

- `text` — the fact, one sentence, imperative where possible. This is what gets injected.
- `why` — the evidence. Critical for global facts: a "workaround" may just have been a bad
  argument, and a 2023 limitation may not hold in 2024. `why` + `revit` (version string when
  version-specific, else null) let facts be re-verified or expired instead of trusted forever.
- `source` — `user-correction` | `llm-discovery` | `auto-capture` | `seed`.
- `verified` — auto-captured candidates start `false`; everything the LLM/user saved starts `true`.
- `deleted` — tombstone. Forgetting appends a `{"id": …, "deleted": true}` line; the store
  takes the last line per id. Append-only keeps writes crash-safe (same rationale as plan 06)
  and lets a future seed-pack merge respect user deletions.

`MemoryStore` API (all static, lock-guarded, swallow-all): `Append(scope, entry)`,
`GetActive(scope)`, `Tombstone(scope, id)`, `RenderForPrompt()` (§3), `ClearAll()`.

## 2. Capture

### 2a. The `remember` command (phase 1)

`Services/Claude/Commands/RememberCommand.cs`:

- `remember user <text>` / `remember global <text>` — optional `--why <reason>` tail.
- Returns the saved id so the model can reference/undo it.
- Marked non-modification in the registry sense where applicable (it does not touch the Revit
  document — no transaction, no undo entry; it writes only to the memory folder).
- System-prompt guidance (one BEHAVIOR bullet added in both prompt builders): save a `user`
  fact when the user corrects you or states a lasting preference; save a `global` fact when an
  approach failed and a different one worked, phrased impersonally with no project/user names;
  save sparingly — lasting facts only, never session trivia.

### 2b. Auto-capture candidates (phase 2)

- Hook: the in-app backends' tool-execution path (`ChatBackendBase.ExecuteTool`) tracks per-turn
  results; on turn end, a failed command followed by a successful different command is appended
  to `memory\candidates.jsonl` with both commands + error text in `why`, `verified:false`.
- Candidates are inert until promoted: `memory promote <id>` moves one into `global.jsonl`.
  This keeps transient errors, typos, and user mind-changes out of prompts by default.
- External curl sessions bypass `ExecuteTool`, so their pairs aren't auto-captured — acceptable;
  external Claude Code can call `/remember` explicitly (its system prompt says so).

## 3. Injection & token budget

- `ChatBackendBase.BuildSystemPrompt()` and `ClaudeCodeBackend.BuildSystemPromptText()` both
  append a `MEMORY:` section from `MemoryStore.RenderForPrompt()`:
  first all active global facts (curated, expected small), then user facts newest-first,
  hard-capped at ~2,000 characters total with a "(+N older facts omitted)" marker. Facts render
  as one-line bullets; `why` is NOT injected (budget) — it lives on disk for curation.
- Empty store → no section at all (zero regression for fresh installs).
- Relevance filtering (inject only facts related to the current task) is explicitly out of
  scope for v1; the cap + curation keep the section small. Revisit if global grows past ~50 facts.

## 4. Management surface

- `memory` command (`MemoryCommand.cs`): `memory list [user|global|candidates]` (ids + text),
  `memory forget <id>`, `memory promote <id>` (phase 2), `memory export` (prints the folder
  path + file list — the "export" until plan 06/09 land; files are plain JSONL a user can
  attach as-is).
- Settings (`SettingsManager` + `SettingsDialog`): "Memory" toggle (default **on**, mirrors the
  plan-06 decision — disclosure line in Settings, off = no capture and no injection), plus a
  "Clear memory…" button calling `ClearAll()` after confirm.
- `CLAUDE.md` + README: document commands, storage path, scopes, privacy posture.

## 5. Privacy

- `profile.jsonl` is personal and **never** leaves the machine through any automated path; the
  seed pack (plan 09) ingests curated `global.jsonl` only, via a maintainer review step.
- Global-scope prompt guidance forbids personal/project-identifying text; the plan-09 curation
  pass is the enforcement backstop before anything ships in the repo.
- Reuse the redaction idea from plan 06 §5 on `remember` input: strip API-key-shaped tokens and
  anything matching `VIBEMODEL_TOKEN` before writing.
- Everything is plain text the user can open, edit, or delete by hand — no opaque state.

## 6. Phasing

**Phase 1 — store + remember + injection (the core).**
`MemoryStore`, `RememberCommand`, `MemoryCommand` (list/forget/export), prompt injection in both
builders + the BEHAVIOR bullet, Settings toggle + Clear, docs. Delivers the user-visible loop:
correct it once, it stays corrected.

**Phase 2 — auto-capture + promote.** Turn-scoped failure→workaround candidate detection in
`ChatBackendBase.ExecuteTool`, `candidates.jsonl`, `memory promote`.

**Phase 3 — seed pack (separate: plan 09).** Export → curate → ship in repo → first-run inherit
+ update-merge semantics. Designed in its own session; depends on the §1 entry format (id-stable,
tombstoned) which is why tombstones are in v1.

## 7. Files touched

**New**
- `src/VibeModel/Infrastructure/MemoryStore.cs`
- `src/VibeModel/Services/Claude/Commands/RememberCommand.cs`
- `src/VibeModel/Services/Claude/Commands/MemoryCommand.cs`
- `src/VibeModel.Tests/MemoryStoreTests.cs` — entry round-trip, tombstone last-wins, cap/render,
  redaction, corrupt-line tolerance (skip bad lines, never throw).

**Modified**
- `ChatBackendBase.cs` — append MEMORY section in `BuildSystemPrompt()`; (phase 2) per-turn
  result tracking in `ExecuteTool`.
- `ClaudeCodeBackend.cs` — same section in `BuildSystemPromptText()`.
- `SettingsManager.cs` / `SettingsDialog.cs` — toggle, Clear button, disclosure line.
- `CLAUDE.md`, README.

## 8. Risks & test posture

- **Prompt bloat / model over-saving.** Mitigated by the 2KB cap, "save sparingly" guidance,
  and `memory list`/`forget` visibility. Watch real usage; add relevance filtering only if needed.
- **Stale or wrong global facts.** `why` + `revit` fields + curation; auto-candidates never
  inject without promotion. Facts are advice to the model, not enforcement — a wrong fact
  degrades gracefully (the model can still try the "impossible" thing).
- **Zero hot-path cost.** Injection reads a small cached in-memory list (reloaded on file
  mtime change); `remember` writes are a single append off the Revit main thread. Consistent
  with the ExternalEvent-latency-dominates constraint.
- **Headlessly testable.** `MemoryStore` and prompt rendering are pure — existing xUnit suite
  (`dotnet test VibeModel.sln -p:RevitVersion=2023`) covers them without Revit. The command +
  injection loop gets a live verify against the embedded HTTP server when phase 1 builds.
