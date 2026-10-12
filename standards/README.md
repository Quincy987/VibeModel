# Office standards packs

A **standards pack** teaches VibeModel how *your office* models: which phases new work goes in,
which view templates and title blocks to use, where the family library lives, which rules the
model is audited against. It is plain data — markdown and JSON — so the person who owns the
office Revit standard (the application manager) can maintain it without touching code.

`example-office/` is a complete, fictional sample. Real office packs contain internal network
paths and are **not** committed to this public repository; keep them on the office share or in
`%LOCALAPPDATA%\VibeModel\standards\` (see *Where packs are found*).

## What VibeModel does with a pack

| Mechanism | Command | Effect |
|---|---|---|
| Template facts | `template` | Reads phases, view templates, scope boxes, coordinates, families, sheets live from the open model, and tells which pack's **fingerprint** it matches. |
| Rules in every chat turn | — | The matching pack's `core.md` (≤ `coreMaxBytes`) and a compact template summary are added to the AI system prompt. |
| Rule sheets on demand | `standards`, `standards <topic>` | Full topic sheets, read only when a task touches them. |
| Default-correct creation | `wall`, `floor`, `place`, `placeid`, `room` (+ `--phase`), `phase` | New elements get the pack's default phase (`phaseTable.default`); `--phase existing\|new\|temp\|demolish\|future` overrides. |
| Office views and sheets | `view`, `sheet` | New views get the pack's view template, phase, phase filter and browser-folder parameters; new sheets get the default title block. Every choice is reported. |
| Library | `library <terms>`, `loadfamily "<path>"` | Searches a cached index of `libraryPaths`; `place` suggests a library family when the requested one is not loaded. |
| Underlays | `linkcad`, `linkrvt`, `coords` | Links (never imports) with the pack's link settings, pins; RVT/IFC by shared coordinates (refused while the model has none, unless `--origin`); reports the coordinate setup. |
| Audit | `check`, `check --stats` | Runs `checks.json`; after every modifying command the element-level rules run on what was just created and a one-line summary is appended. |
| Source documents | `standards --index`, `standards docs`, `standards <doc> <chapter>`, `standards distill` | Indexes the office manuals (PDF/txt) by chapter; optional LLM distillation into `core.generated.md`. |

Nothing changes for a model whose fingerprint matches no pack: commands behave exactly as
without standards.

## Where packs are found

First occurrence of a pack `id` wins:

1. **Setting `StandardsPackPath`** (VibeModel Settings → Office standards) — a pack folder, or a
   folder containing pack folders. Point this at the office share to give every modeller the
   same pack without an add-in release. A **network** path (UNC or mapped drive) is never read
   on Revit's thread: at startup, on `standards --refresh` and on Settings → Save it is copied in
   the background to `%LOCALAPPDATA%\VibeModel\standards\_mirror\`, and VibeModel reads that
   mirror — so a missing VPN costs nothing, the last synced pack simply stays in force.
2. `%LOCALAPPDATA%\VibeModel\standards\<pack>\pack.json` — a local working copy.
3. `<add-in folder>\standards\<pack>\pack.json` — packs shipped with the add-in.

`standards` lists every folder it looked at and why a pack was skipped or not active.
After editing a pack, run `standards --refresh` (or reopen Settings and Save).
`standards --prompt` shows exactly what the chat receives this turn.

Derived data (library index, document index, generated core, check log, additions copy) is cached per pack in
`%LOCALAPPDATA%\VibeModel\standards\_cache\<pack id>\`.

## pack.json

```jsonc
{
  "id": "example-office",            // unique; folder name if omitted
  "name": "...", "version": "2026-10-11.1", "owner": "...",
  "sources": [{ "title": "...", "date": "...", "path": "..." }],   // indexed by `standards --index`
  "fingerprint": {                   // when does this pack apply to a document?
    "phases": ["1. Existing", "2. New construction", "3. Final"],  // ALL must exist
    "sharedParameters": ["EX_ObjectCode"],                         // at least ONE must be bound
    "scopeBoxes": [...], "views": [...], "viewTemplates": [...],   // supporting evidence only (projects delete/rename these)
    // neither phases nor sharedParameters declared → the pack applies to EVERY document
    "aliases": { "Project_extent_WCS": ["Project_Extent_WCS"] }    // alternative spellings
  },
  "core": "core.md",                 // or "generated" to use core.generated.md from `standards distill`
  "coreMaxBytes": 4096,              // hard cap for the per-turn STANDARDS block
  "additionsFile": "additions.md",   // optional extra rules, appended to the core (see below)
  "topics": { "phasing": "topics/phasing.md", ... },
  "phaseTable": {
    "default": "new",
    "entries": { "new": { "created": "2. New construction", "demolished": null }, ... },
    "aliases": { "temporary": "temp" }
  },
  "viewDefaults": {
    "plan":   { "template": "EX_W_C_Plan", "phase": "new", "phaseFilter": "...", "parameters": { "EX_BrowserFolder": "01_Work views" } },
    "threeD": { ... },
    "section": { ... }
  },
  "familyPrefixes": ["EX_", "NLRS_", "GM_"],
  "libraryPaths": ["\\\\server\\revit\\library\\"],
  "checks": "checks.json",
  "sheets": { "defaultTitleblock": "EX_TITLEBLOCK : A1", "stampParameters": ["EX_META_*"] },
  "coordinates": { "roundToMm": 1000 },
  "linkCad": { "pinAfterLink": true, "correctOffAxisLines": false, "orientToView": false, "refuseFirstDgnReason": "..." },
  "export": { "dwgSetup": "EX_DWG export" },          // `export dwg` uses this saved DWG export setup (--setup overrides)
  "standardsFolder": "\\\\server\\revit\\standards\\",   // default for the Settings "source documents folder"
  "distill": { "sources": [                            // what `standards distill` reads, MOST IMPORTANT FIRST
    { "document": "modelling manual", "chapters": ["4.2", "2.1", "1"] },  // file-name part; a chapter includes its subchapters
    { "document": "checklist" }                                          // no chapters = the whole document
  ] }                                                  // shorthand: { "chapters": [...] } = those chapters of every document
}
```

Every section is optional. Missing files or sections degrade to "not available" — a broken pack
is skipped and reported by `standards`, never fatal.

## checks.json

```json
{ "rules": [
  { "id": "param-filled", "kind": "param-filled", "severity": "warn",
    "message": "Checklist E1: object code filled on new work",
    "fix": "Fill EX_ObjectCode with 'set <id> EX_ObjectCode <code>'.",
    "params": { "parameters": ["EX_ObjectCode"], "phaseKey": "new" } }
] }
```

`severity`: `warn` | `fail` | `info`. `enabled: false` switches a rule off. Rule **kinds** and
their parameters:

- `pinned` — `targets`: `cadLinks`, `pointClouds`, `grids`, `revitLinks`; `requireSetup` (default true: skip until a link or shared coordinates exist).
- `phase-not-none` — model elements without a creation phase.
- `phase-filter-digit` — a view's phase and phase filter must start with the same digit.
- `view-phase-for-new-work` — the active view is on the pack's *existing* phase.
- `view-template-required` — `namePattern` (regex for plot views), `onSheets` (views placed on sheets).
- `family-source` — model families without one of `prefixes` (default: `familyPrefixes`); `ignoreCategories`; `onlyPlaced` (default true: only families with instances — a template's unused families are not the modeller's choice).
- `param-filled` — `parameters` empty on elements created in the phase of `phaseKey` (default `new`).
- `survey-point-origin` — the survey point's own shared coordinates must read 0,0,0 (`toleranceMm`); its distance from the internal origin is not judged (acquiring coordinates legitimately moves it far away).
- `base-point-rounded` — `roundToMm` (only once shared coordinates exist).
- `base-point-pinned` — `requireSetup`.
- `file-name` — `pattern` (regex on the file name); always flags Revit backups `*.0001.rvt`.
- `schedule-phase` — `namePattern`, `phaseKey`, `phaseFilter`.
- `links-are-links` — imported (instead of linked) CAD.
- `project-north-true` — the project base point's *Angle to True North* must be 0 (`toleranceDeg`, default 0.01): Project North never rotates; a straight view comes from a scope box or crop region.
- `reference-point-true-north` — instances of the family whose name contains `familyPattern` (required — the family name is office-specific) must be unrotated and pinned (`toleranceDeg`). While Project North itself is rotated it reports INFO instead of judging.
- `links-by-shared-coordinates` — once the model has shared coordinates, every Revit link must sit where *Auto – By Shared Coordinates* puts it (`toleranceMm` default 1, `toleranceDeg`). Unloaded links and CAD links (the API keeps no record of their placement) are counted as not verifiable, never flagged. A shared link that is later *dragged* is not flagged either: Revit moves the link's own shared position along (and asks to save it on close), so it still reads as shared.

Element-level kinds (`phase-not-none`, `param-filled`, `family-source`, `pinned`,
`reference-point-true-north`, `links-by-shared-coordinates`) also run after
each modifying command on exactly the elements it created.

## Updating a pack (application manager)

1. Edit the files; bump `version` in pack.json.
2. Re-measure the template after a template change (`template` in a fresh model from it) and
   update fingerprint names — always the *measured* spelling, with the manual's spelling as alias.
3. `check --stats` shows which rules fire most across sessions: candidates for a better default
   or a clearer core rule in the next version.
4. Optional additions file (`additionsFile`, default `additions.md`) next to pack.json or in the
   source-documents folder is appended to the core verbatim — for decisions not (yet) in the
   manual. The copy in the source-documents folder is picked up at the next document index run
   (Revit startup or `standards --index`).
5. Keys VibeModel does not know (e.g. `template`, `paths`, phase-table `label`s) are ignored —
   use them freely as documentation.
