# Plan 11 — verification in Revit 2023 (2026-10-11)

Live verification of the office-standards build against the pilot office's real Revit template
(opened as a document, never saved), with the pilot pack installed in
`%LOCALAPPDATA%\VibeModel\standards\`. The pilot pack and the raw command transcripts contain
internal names and network paths, so they are kept outside this public repo
(`docs/verify/*.local.md`, gitignored); this file summarises them with office names generalised.

Environment: Revit 2023.1, add-in built from this branch and deployed with `build.ps1`; the
office network share was **unreachable** during the whole run (weekend, no VPN), which also
exercised every offline path. Seven rounds: round 1 found 4 defects (fixed), round 2 ran the
full script, rounds 3–4 re-checked the fixes, rounds 5–7 verified the two code-review passes below on
fresh Revit sessions. Unit tests: 296/296 pass.

## Results

| Area | What was checked | Result |
|---|---|---|
| Fingerprint | `template` on the office template | Matched 3/3 (phases, scope box via measured spelling, shared parameters) + 3/3 view templates, 4/4 office views. 33 view templates grouped by prefix, 10 phase filters, active view phase, coordinates, 138 families by prefix class. |
| Fingerprint negative | Pack fingerprint given a phase the document lacks | `template: unknown (no pack matches) — missing phase(s) '…'`; `wall` output byte-identical to pre-plan-11 (no phase line, no check line); prompt block says "none matched". |
| Switched off | `StandardsEnabled=false` + `standards --refresh` | Overview says switched off; `wall` has no phase line; `check` → `NO_PACK`. |
| Prompt blocks | `standards --prompt` | TEMPLATE 1018 B (cap 1024), STANDARDS 3515 B (cap 4096); TEMPLATE flags that the active work view is on the *existing* phase. |
| Default phase | `wall`, `floor`, `placeid` with no flag | Phase Created = new-work phase, Demolished = None — **read back from the element via `exec`**, not from the command's own output. Note line + "not visible in the active view" warning (view on phase 1). |
| `--phase` | `--phase temp`, alias `--phase tijdelijk`, `--phase bogus` | temp → created + demolished in phase 2 (read back); alias resolved; bogus → `ERROR: Unknown phase kind` + hint listing keys and aliases. |
| `phase` command | `phase <id> existing` on the temp wall | `2/2 → 1/None` (read back). |
| JSON | `wall …&format=json` | Additive `check` object next to `meta`. |
| Batch | `POST /batch` with two walls | One undo unit; single trailing `-- check: 1 WARN (param-filled ×2)` line. |
| `view` | `view plan`, `view 3d`, `--no-template`, `--template X`, `--template Nope`, `--name` | Office work template applied, phase 2 + filter 2.3 set, browser-folder parameters reported as template-controlled (set explicitly with `--no-template`); unknown template → error; `--name Werkview bouwfase …` (multi-word, unquoted) names the view, duplicate name → error (round 4). |
| `sheet` | `sheet` without args | Office title block (`… : A0`, office default) + stamp-field hint. |
| `check` | Full audit on the fresh template | 0 FAIL / 3 WARN / 7 OK / 3 skipped: BIM-ID empty on new work, active view on existing phase, file name (`.rte`). Pins/base point/rounding correctly **skipped** until something is linked. |
| `check` after linking | after `linkcad` | `pinned` now runs (grids unpinned), `base-point-pinned` runs; the new link itself is pinned. |
| Post-command check | every modifying command | Only problems, only on the created elements. |
| `check --stats` | hit log | Rule counts aggregated from `check-log.jsonl`. |
| Library | office share unreachable | `INDEX_NOT_READY … library unreachable` (never blocks); after the fix an index built from *other* folders is no longer served (round 3). |
| Library (reachable) | pointed at a local folder with real office pile families incl. Revit backups | Backups skipped; `library funderingspaal` → 3 hits; `loadfamily` loaded 39 types; `placeid` → Structural Foundation footing on phase 2 (read back). |
| `place` fallback | family not loaded | Error + library hint ("No match in the office library either" / `loadfamily "<path>"` suggestions) — after the round-1 fix. |
| `linkcad` | `.dgn` as first underlay; a DWG with `--first` | DGN refused with the pack's reason; DWG **linked** (not imported), off-axis correction off, orient-to-view off, centre-to-centre (no shared coordinates yet), pinned. |
| `coords` | before/after link | Survey point at origin, base point 0/0 unpinned, no shared coordinates; link count + pins. |
| Source documents | share unreachable, then a local folder with a .txt and a generated .pdf manual | Unreachable sources reported (count + examples); local: 2 documents indexed, TOC page skipped, chapters `1, 2, 2.1, 2.2, 3`; `standards <doc> 2` returns 2 + 2.1 + 2.2; PDF text via the out-of-process helper. |
| Distill | `standards distill` (Claude Code CLI) | `core.generated.md` written with source hashes + chapter list; reported as *not in use* until pack.json says `"core": "generated"`. (Test output deleted afterwards.) |
| Chat turn | One turn run exactly like the chat pane's Claude Code backend (same CLI, tools and appended prompt incl. TEMPLATE/STANDARDS), Dutch request: temporary cofferdam wall + new floor + a work view + check | The model used `wall … --phase temp` and `floor … --phase new` unprompted, created the view with `view plan` (office template, phase 2 / filter 2.3), confirmed both elements are visible in it, ran `check` and proposed filling the BIM-ID parameters. It struggled to rename/activate the view through `exec` → `view --name` added. |

## Defects found and fixed during verification

1. `place` (by family/type name) did not pass the phase request nor the library hint — an edit
   had not applied to that CRLF file. Fixed; re-verified.
2. `family-source` flagged ~50 unused families that ship with the template (mullions, rebar
   shapes, balusters). Now checks **placed** families by default (`onlyPlaced`, default true).
3. `standards --refresh` did not re-read settings.json (settings cache) — the disabled and
   standards-folder tests silently used old values. `Reset()` now clears the settings cache.
4. A library index built from other folders (after `libraryPaths` changed) was still served.
   Now discarded and rebuilt.
5. `view --name` took only the first word of an unquoted multi-word name. Flag values now run
   to the next `--` flag (`ViewArgs`, unit-tested); re-verified in round 4.

Also: unreachable-source status shortened to a count + 2 examples; phase note uses the
platform newline; `standards --prompt` added to show exactly what the chat receives.

## Code-review fixes (verified in rounds 5–6)

- **Network pack folder.** A network `StandardsPackPath` was read on Revit's main thread (pack
  files, plus an additions file in the share) — a dead VPN could freeze Revit. Now a background
  sync mirrors the share to `%LOCALAPPDATA%\VibeModel\standards\_mirror\` and only the mirror is
  read. Round 6: setting pointed at a UNC path to this machine's admin share (`\\localhost\c$\…`); `--refresh` reported "not
  mirrored yet — copied in the background", seconds later the share's pack (version
  `mirror-test`) was active from the mirror and the local copy was reported as shadowed;
  clearing the setting brought the local copy back.
- **`survey-point-origin` false FAIL.** The rule judged the survey point's distance from the
  internal origin — which is large on every correctly georeferenced project. It now judges the
  survey point's own shared coordinates (0,0,0). Round 5: `coords`/`check`/prompt say "survey
  point at shared 0,0,0".
- **Rules twice in the CLI prompt.** The office blocks went into both the appended system prompt
  and the commands file the model reads; now only the appended prompt.
- **Scoped `pinned` rule** nagged after `grid` on a project without coordinates; it now follows
  the same precondition as the full audit (round 5: `grid` → no check line).
- Smaller: post-command check only after a successful command; prompt re-render throttled to
  every 3 s after edit bursts; registry generation invalidates cached verdicts; a share that timed
  out is probed once per index run; documents removed from a reachable folder leave the index;
  the additions file name is a pack setting (`additionsFile`); dead members removed; one shared
  Claude CLI lookup.

## Second review pass (verified in round 7)

- `linkcad` refused a DGN only when NO underlay existed; it now also waits until shared
  coordinates are acquired (`--force` overrides). Round 7: DGN refused as first underlay, DWG
  linked centre-to-centre and pinned, DGN then refused "before shared coordinates are set up".
- A phase Revit refuses no longer leaves an element half-changed: the previous phases are
  restored and the failure reported (also per element by `phase`). Round 7:
  `wall … --phase demolish` → `1 / 2`, `phase <id> future` → `3 / None` (read back).
- The STANDARDS block's byte budget now reserves room for the footer, so a small `coreMaxBytes`
  cuts the core rules instead of the phase hint (unit-tested).
- The distiller locates the Claude CLI on its worker thread instead of inside the command on
  Revit's thread; `GetActivePack` costs nothing when no pack is installed; the Settings dialog
  scrolls instead of overflowing small screens (not visually verified).

## Not verified here

- The Settings dialog's new *Office standards* section (WPF; not driven by automation to avoid
  clicking on the user's desktop) — compiled, not exercised visually.
- The real office share (K:) paths: library scan, manual PDFs and shared-coordinate workflow on
  a real project, because the share was unreachable. Every offline path was exercised instead,
  and the share mirror was exercised through a UNC path to this machine.
- `survey-point-origin` on a georeferenced project (no such model available); the template
  case and the rule's unit tests pass.
- Revit 2024/2025 builds.
