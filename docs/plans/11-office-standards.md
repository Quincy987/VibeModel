# Plan 11 — Office Standards: an office's Revit standard as default knowledge

> Goal: modellers correct VibeModel less often because the add-in already knows *how this
> office models*: which template, which phases, which families, where the library is, how
> coordinates are set up, what to model in 3D. The pilot office's modelling manual is the
> knowledge source, but the design is office-agnostic: the manual becomes one **standards
> pack** (data), not code.

> **STATUS (built, phases 1–4).** Implemented on branch `claude-bot/t-300`; verification
> evidence in `docs/verify/plan11-verify.md`. The pilot office's real pack contains internal
> network paths and staff names and is **not** in this public repo: it lives in
> `%LOCALAPPDATA%\VibeModel\standards\<pack>\` for now and moves to the office share by
> pointing the `StandardsPackPath` setting at it. The repo ships a fictional
> `standards/example-office/` with the same structure, and `standards/README.md` documents the
> format. Complements plans 08 (memory = learned corrections) and 10 (project folder =
> documents); this is the **office layer** between those two.

## TL;DR — four mechanisms, in order of leverage

1. **Read the template live, don't transcribe the manual.** Most of what an office template
   defines (phases, view templates, scope boxes, shared parameters, loaded families, sheets) is
   *in the document*. `template` introspects it; a compact summary is injected per turn.
2. **Ship the rules that aren't in the document as a versioned standards pack** — `pack.json`,
   an always-on `core.md`, on-demand topic sheets, `checks.json`; owned by the office's Revit
   application manager, read from a configurable folder.
3. **Make commands default-correct** — creating commands set the office phase, `view` applies
   the office view template, `place` falls back to the office library, `linkcad` uses the
   office link settings and pins. A correct default beats advice in a prompt.
4. **Lint against the rules** — `check` audits the model; after every modifying command the
   element-level rules run on what was just created, so VibeModel corrects itself before the
   modeller has to.

## Why not inject the manual?

- A 60-page manual is ~15k tokens per turn of mostly screenshots-in-prose and click paths.
- Half of it describes the template — which is open in front of us; reading it is exact, the
  manual already lags (measured: several literal names in the manual differ from the template).
- Hardcoding one office's phase names in C# would make the add-in useless for every other office.

So: template facts come from the document, rules come from a pack, and the pack is data.

## Layered context model

Every turn's system prompt gets, in this order (each layer optional and bounded):

| Layer | Source | Size |
|---|---|---|
| Built-in | prompt builders | fixed |
| **TEMPLATE** | live introspection of the open document (incl. the active view's phase) | ≤ 1 kB |
| **STANDARDS** | active pack's core rules + topic list + phase hint | ≤ `coreMaxBytes` (default 4 kB) |
| Project folder / memory | plans 10 / 08 | bounded |

The blocks are rendered on Revit's main thread (Idling, only when something changed: document
edit, view switch, open/close, settings change; at most every 3 s after a burst of edits) and
published to a lock-free holder that the chat backends read on their worker threads. The Claude
Code backend puts them in the `--append-system-prompt` only — not in the commands *file*, which
the model also reads (it would get the rules twice). `standards --prompt` shows the exact text.

## Activation: fingerprint

A pack applies to a document when the document has **all** the pack's phases, **at least one**
of its scope boxes and **at least one** of its shared parameters (each criterion only when
declared; views and view templates are reported as supporting evidence but never block — a
pack that declares none of the three required criteria applies to every document).
Names are matched case-insensitively and through declared aliases — fingerprints use the
*measured* template strings, with the manual's spellings as aliases. Among matching packs the
most specific fingerprint wins. A document that matches no pack gets exactly the pre-plan-11
behaviour: no blocks, no defaults, no check lines, byte-identical command output.

## Where packs live

Search order (first pack id wins; later copies are reported as *shadowed*):

1. Setting **`StandardsPackPath`** — a pack folder or a folder of packs (the office share later).
   A network path is never read on Revit's thread: a background sync (startup, `--refresh`,
   Settings → Save) copies the packs to `%LOCALAPPDATA%\VibeModel\standards\_mirror\` and
   discovery reads that mirror; an unreachable share keeps the last mirror in force;
2. `%LOCALAPPDATA%\VibeModel\standards\*\pack.json` (local working copy — today's home);
3. `<add-in folder>\standards\*\pack.json`.

Settings also has **`StandardsFolder`** (the office source documents, e.g. the folder with the
manual PDFs) and **`StandardsEnabled`** (master switch). Derived data is cached per pack under
`%LOCALAPPDATA%\VibeModel\standards\_cache\<pack>\` (library index, document index, generated
core, check log, additions copy). Revit's main thread only reads local files, plus the two
explicit actions on a share path (`loadfamily`, `linkcad`) which probe with a 3 s deadline.

## Source documents instead of a hand-typed copy (§4.0 of the original design)

`standards --index` lists the PDFs/.txt at the top of the standards folder plus the pack's
declared sources, extracts text per page, splits it on the manual's own numbered headings
(only plausible successors count, TOC pages are skipped) and caches `<hash>.json` per file —
an unchanged file is never re-read, an unreachable share falls back to the cache. PDF text comes
from `VibeModel.PdfText.exe` (PdfPig) in a separate process so its dependencies never load into
Revit. `standards docs` / `standards <doc> <chapter>` read chapters verbatim.

`standards distill` (explicit only) sends the pack's `distill.chapters` through the Anthropic API
(API key set) or the Claude Code CLI with a fixed prompt ("≤25 imperative one-liners with exact
names, no procedures") and writes `core.generated.md` with source hashes; the pack uses it when
`"core": "generated"`. The hand-written `core.md` remains the reference to compare against.
An optional additions file (pack key `additionsFile`, default `additions.md`; the pilot uses
`aanvullingen.md`) next to pack.json or in the standards folder is appended to the core verbatim —
for werkgroep decisions not yet in the manual. The standards-folder copy is cached locally by the
background index run.

## Commands

- `template` — fingerprint verdict (or why no pack matched), phases + filters, active view,
  view templates grouped by prefix, scope boxes, office views present, coordinates and pins,
  CAD links vs imports, family prefix histogram, materials, bound parameters (office ones ✓/✗),
  sheets, title blocks.
- `standards [topic|core|docs|<doc> <chapter>|--refresh|--index|--prompt|distill [--status]] [--pack id]`.
- `phase <ids|selected> <kind>` — office phase table applied to existing elements.
- `check [all|selected|<ids>] [--stats]` — rule kinds: `pinned`, `phase-not-none`,
  `phase-filter-digit`, `view-phase-for-new-work`, `view-template-required`, `family-source`,
  `param-filled`, `survey-point-origin`, `base-point-rounded`, `base-point-pinned`, `file-name`,
  `schedule-phase`, `links-are-links`. Preconditions keep a fresh template quiet (pins only once
  a link or shared coordinates exist). `survey-point-origin` judges the survey point's own shared
  coordinates (0,0,0), not its distance from the internal origin — acquiring coordinates moves it
  far away on every correctly georeferenced project.
- `coords`, `library <terms>` / `library --rebuild`, `loadfamily "<path>"`,
  `linkcad "<path>" [--first] [--origin] [--no-pin]`.
- Changed: `wall`, `floor`, `place`, `placeid`, `room` (`--phase`, default phase, visibility
  warning), `view` (office template/phase/filter/browser parameters, `--template`,
  `--no-template`, `--name`; falls back to a structural plan type when no floor-plan type exists),
  `sheet` (default title block, stamp-field hint).

## Decisions taken while building (deviations from the original design)

- **Core cap 4 kB instead of 1.5 kB**, configurable per pack (`coreMaxBytes`): the pilot's
  reviewed core is ~3 kB; cutting it in half would drop rules. The template block keeps 1 kB.
- **Grids, levels and tags are not phased** — Revit datums and annotations have no phases, so
  they are not wired to `--phase`. `room` is wired but Revit does not allow re-phasing rooms;
  an explicit `--phase` reports "not applicable".
- **The post-command check runs after single modifying commands too**, not only batches —
  the chat backends mostly issue single commands. It only reports problems, only for
  element-level rules, only on the elements the command added (DocumentChanged added-ids).
- **Distillation is explicit** (`standards distill`), never automatic on a hash change: it costs
  an LLM call and its output should be reviewed before switching a pack to `"core": "generated"`.
- **PDF extraction out of process** to avoid assembly-binding conflicts inside Revit.

## Risks

- **Pack goes stale vs template.** Template facts are read live; the pack carries `version` and
  `sources`; fingerprint mismatch disables it and `standards` says why.
- **Wrong defaults are worse than none.** Defaults only fire for a matching fingerprint, and
  every automatic choice is printed in the command output.
- **Network drive.** The pack is mirrored, library and documents are indexed in the background
  with deadlines (one timed-out share is not probed again per run) and cached.
- **Ownership.** When the pack moves to the office share, the application manager owns it;
  until then the local copy is maintained by the VibeModel maintainer.
