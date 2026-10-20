# Plan 18 — Known API gaps: say what Revit's API cannot do, at the moment it matters

> Goal: when the AI runs into something the Revit API genuinely cannot do — a label in a tag,
> generic-annotation or title-block family, a legend view, a design option, a phase — VibeModel
> names the gap and the workaround that was measured, in the answer the AI is already reading
> (`apilookup`, and the hint under a failed `exec` compile). No prompt rule (one clause in
> `apilookup`'s tool description is the only prompt-visible change).

> **STATUS (built 2026-10-20, branch claude-bot/t-362).** `KnownApiGaps` + the `apilookup` /
> compile-hint hooks; headless tests in `KnownApiGapsTests` (fixture stand-ins for the Revit types).
> Live checks: see "Verification" below.

> **Origin (2026-10-20).** A colleague on v1.2.10: new shared parameters on structural rebar, read
> out with tags. The chat created the shared parameters, bound them, made the tag types — and could
> not make the labels in the tag family. Asked for help with the family open, it copied the label
> closest to each one he needed and wrote next to it what the label should show, so he could finish
> in the Edit Label dialog. Last week the same with a Generic Annotation (`(LABEL Betonkwaliteit)`
> placeholders). The parameters it *could* add. He was not blocked; he was puzzled.

## What was measured (Revit 2023.1.2, 2026-10-20, `demo_out/labelprobe/probe.rvt`, IB families)

- **No API creates a label.** `Autodesk.Revit.Creation.FamilyItemFactory` has `NewModelText`,
  `NewDetailCurve`, `NewDimension` … and no `NewLabel`; there is no `Label` class at all (the only
  hits for "Label" are `LabelUtils`, `LabelType`, analysis-display enums). `apilookup Label` today
  answers "matches several types" with those five names — true and useless.
- **A label is a `TextElement` without a category** (not a `TextNote`; `TextNote` is the plain
  text). The rebar tag has 1, the title block 18 (+ 23 text notes), the multi-category tag 15.
  Which parameters it shows is readable only through the read-only parameter **Sample Text**
  (`TAG_SAMPLE_TEXT`: `Sample Quantityx Type Name-Spacing`, `Opdrachtgever`, `Family NameComments`).
- **Rebinding is impossible.** The `Label` parameter (`TAG_TAG`) has `StorageType.None`,
  `IsReadOnly = false`, and `Set(string)`, `Set(ElementId)`, `Set(int)` all **return true and
  change nothing** — Sample Text is identical after `Regenerate()`. A snippet that "succeeds" here
  has done nothing; this is the trap to warn about.
- **What does work, in one transaction on the family document**
  - `FamilyManager.AddParameter("Betonkwaliteit", GroupTypeId.Text, SpecTypeId.String.Text, false)`.
  - `ElementTransformUtils.CopyElement(famDoc, labelId, offset)` — the copy is a `TextElement` with
    the **same Sample Text**: it keeps its parameter, and is the right starting point for the user.
  - `ElementTransformUtils.CopyElements(srcView, ids, dstView, Transform.Identity, options)` from
    **another family of the same category** (Generic Annotation → Generic Annotation: the copy
    arrived with Sample Text `B-01` and its parameter) — for a family that has no label yet.
    Across categories Revit rolls back: *"Can't paste Labels across Families of different
    Categories."*
  - `TextNote.Create(famDoc, viewId, point, "(LABEL Betonkwaliteit)", famDoc.GetDefaultElementTypeId(
    ElementTypeGroup.TextNoteType))` next to the copy — the placeholder the colleague invented.
  - `doc.EditFamily(family)` from exec needs `trans.RollBack();` first (the project's transaction
    must not be open); the family document takes its own `Transaction`; then
    `fd.LoadFamily(doc, new VibeModel.Services.Helpers.KeepValuesLoadOptions())` and `fd.Close(false)`
    (the first draft only closed it — work lost; see the review round below).
- **The step that stays manual**: (Edit Family first, when it was prepared from the project) select
  the copied label → *Edit Label* → pick the parameter (one dialog per label), then Load into
  Project. Everything before and after it is API work.
- **Also no API** (checked by reflection on RevitAPI.dll 2023): `DesignOption` and `Phase` have no
  creation member; legend views have no class and no creation call (plan 13 documents the
  "duplicate an existing legend" route). Both worth the same treatment — the model keeps trying
  `Phase.Create` until it reads that nobody can.

## Decisions

- **Not a prompt rule.** It would cost every turn for a case that is rare, the colleague's own
  failure was not retries but surprise, and a blunt "labels are not possible" invites the model to
  stop before the parameters and types it *can* make. The knowledge goes where it is read on demand.
  The only prompt-visible change is one clause in `apilookup`'s description (the tool list), so the
  model has a reason to ask.
- **One pure table, `Services/Claude/KnownApiGaps.cs`**: entries `{ Name, TypeKeys, MemberKeys,
  Title, Cannot, Works, Manual }`. Type keys: `Label`, `Labels`, `TextElement`, `LabelType`;
  `Legend`, `Legends`, `ViewLegend`, `LegendComponent`; `Phase`, `DesignOption`. Member keys (bare or
  `Type.Member`): `NewLabel`, `CreateLabel`, `FamilyItemFactory.NewLabel`, `TAG_TAG`;
  `ViewType.Legend`, `ViewFamily.Legend`, `NewLegend`, `CreateLegend`; `Phase.Create`,
  `DesignOption.Create`, `NewPhase`, `CreatePhase`, `NewDesignOption`, `CreateDesignOption`.
  `Phase`/`DesignOption` are found, useful types; their note says "exist; cannot be created".
- **Matching follows what the lookup resolved** (review round): a found type by its simple name
  against the type keys only; a found or missing member as `Type.Member` / the member alone against
  the member keys only; nothing resolved (not found, several types) → the query as typed against
  both. Whole dot-separated names, case-insensitive, never a substring. So `LabelUtils`,
  `Phase.Name`, `TextElement.Text` and a property that is merely *called* `Phase` or `Label`
  (`PlanTopology.Phase`) get no block — the first version matched any last name and would have.
- **Hook — `apilookup`** (`ApiLookup.Lookup` = `KnownApiGaps.Attach(LookupApi(…))`): not found,
  ambiguous and "has no public member" get the block right after their first line (before the
  closest names); a found type or member at the end. Text ≤ 12 lines; JSON additive
  `data.knownGap: { name, title, cannot, works: [], manual }`.
- **Hook — exec compile hint**: moved from `ApiLookupCommand` into `ApiLookup.CompileErrorHint`
  (pure, so it is unit-tested on a fixture engine); it quotes `HintLines` (20) lines of each lookup
  plus the length of the gap block when the lookup carries one (`Result.KnownGap`), so the whole
  block is always shown.
- **The silent-success trap** (`Set` on the `StorageType.None` Label parameter returns true) is not
  interceptable in general; the gap note says it.
- **Keeping the work** (review round, measured): `fd.LoadFamily(doc)` without options is refused for a
  family the project has ("Family loading failed."), and an exec snippet cannot declare an
  `IFamilyLoadOptions` class. The keep-values options generated families already used are now public
  (`VibeModel.Services.Helpers.KeepValuesLoadOptions`), and the recipe names both routes: the family
  open in the Family Editor (the colleague's case — `doc` is the family, exec's own transaction keeps
  it) or `EditFamily` from the project + `LoadFamily(doc, new KeepValuesLoadOptions())`.
- **Not doing**: a `label` command (commands run on the project document; the family editor
  session is the user's; one report so far) and UI automation of the Edit Label dialog (fragile,
  the first thing a Revit update breaks). Revisit if the placeholder route is reported as too slow.
  Not measured: the `EditFamily` route under change approval `ask` (exec then runs inside a run
  group, which Revit may refuse `EditFamily` in).

## Files

- `src/VibeModel/Services/Claude/KnownApiGaps.cs` — new, pure: the table, `Match(query, kind)`,
  `Lines` / `Describe` / `Data`, `Attach(result, query)`.
- `src/VibeModel/Services/Claude/ApiLookup.cs` — `Lookup` attaches the gap; `CompileErrorHint`
  (moved here) + `HintLines`; `Result.ResolvedType` / `Member` (what the lookup resolved) and
  `KnownGap`.
- `src/VibeModel/Services/Claude/Commands/ApiLookupCommand.cs` — `Description`; the hint delegates.
- `src/VibeModel/Services/Helpers/KeepValuesLoadOptions.cs` — new, public (was nested, internal, in
  `GeneratedFamilyStore`, which now uses this one).
- `src/VibeModel/Services/Claude/FamilyGen/GeneratedFamilyStore.cs` — `Load(doc, path, name, stamp)`
  writes the new VM_Source stamp itself (both reload sites did or should have done it by hand) and
  `WriteTypeText` is the one "set a text on every project type" loop; `ProfileRunner` passes its
  stamp; `ProfileCommands` passes its stamp and writes VM_Profile through `ProfileStore.WritePoints`.
- `src/VibeModel.Tests/KnownApiGapsTests.cs` — matching per kind, `LabelUtils` / `Widget.Label` /
  `PlanTopology.Phase` non-matches, unique keys, line budget, the recipe text, JSON, the four result
  kinds through a fixture engine, the compile hint.
- `src/VibeModel/Services/Claude/ModelChangeTracker.cs` + `VibeModelApp.cs` — `RecordCommit(…,
  duringVibeModelCommand)` fed with `RevitCommandHandler.IsProcessingCommand` (review round 3);
  `ModelChangeTrackerTests.ForeignNamedCommit_DuringOurCommand_IsOurs`.
- `CLAUDE.md` (stamp and apilookup sections), `CHANGELOG.md`, this plan.

## Verification (live, Revit 2023)

1. `apilookup Label` → the five names **and** the label gap with the copy/placeholder recipe.
2. `apilookup FamilyItemFactory.NewLabel` → "no public member" + the gap.
3. `exec` with `doc.FamilyCreate.NewLabel(v, XYZ.Zero, "x")` → `Hint: apilookup
   FamilyItemFactory.NewLabel` + the gap within the hint's 20 lines.
4. `apilookup Phase` → the type as before + "cannot be created"; `apilookup DesignOption.Create`
   → not found + the gap; `apilookup Legend` → the legend gap.
5. `apilookup LabelUtils`, `apilookup Wall` → unchanged (no block).
6. Run the recipe from the gap text verbatim in exec on `IB_Tag_Rebar_aantal-x-dia-hoh` (copy +
   `TextNote.Create`, rolled back) — it must compile and run as written.

### Result (Revit 2023.1.2, 2026-10-20 11:20, branch build deployed to my own instance) — 10/10

- 1 `apilookup Label` → "'Label' matches several types", the label block right after that line,
  then LabelType / LabelUtils / … as before.
- 2 `apilookup FamilyItemFactory.NewLabel` → "has no public member 'NewLabel'." + the block + the
  `using Autodesk.Revit.Creation;` line (no "Closest:" — nothing is near NewLabel, also live).
- 3 `exec … doc.FamilyCreate.NewLabel(v, XYZ.Zero, "x")` → the Dutch CS1061 error, then
  `Hint: apilookup FamilyItemFactory.NewLabel` with the whole block (header to "Manual step") and
  the using line — 12 lines, inside the hint's budget.
- 4 `apilookup Phase` → the type, its "How to get one" lines, then the phase/design-option block;
  `DesignOption.Create` → no public member + block; `Legend` → legend block; JSON `data.knownGap.name
  = "legends"`. The note's read line ran: `doc.Phases.Size` 3, `OfClass(typeof(DesignOption))` 0.
- 5 `apilookup LabelUtils`, `apilookup Wall` → no block.
- 6 The recipe as written, in exec on `IB_Tag_Rebar_aantal-x-dia-hoh` (rolled back): parameter
  `Betonkwaliteit` added, label copied (copy's Sample Text = the original's), the placeholder note
  `(LABEL Betonkwaliteit)` created next to it.
- Headless: 1509/1509 (`KnownApiGapsTests` 34 cases).

### Review round (Revit 2023.1.2, 2026-10-20 afternoon, my own instance) — 8/8 gap checks, 4/4 after an older profile bug was fixed

- **Found: the recipe threw its work away.** It ended with `fd.Close(false)` and never loaded the
  family back. Measured: `fd.LoadFamily(doc)` without options → `InvalidOperationException: Family
  loading failed.`; with VibeModel's keep-values options the family is reloaded and a re-opened copy
  shows the work (labels 1 → 2, the placeholder note, the parameter). Exec cannot declare an
  `IFamilyLoadOptions` class, so the options moved to the public
  `VibeModel.Services.Helpers.KeepValuesLoadOptions`, and the recipe names both routes. With the
  family open in the Family Editor (the colleague's case) exec's `doc` is the family and its own
  transaction keeps the copy + placeholder (measured: labels 2, placeholder 1, parameter, still there
  in the next call).
- **Found: member names matched type keys.** `PlanTopology.Phase` (a property) and `Widget.Label`
  would have carried a gap block; matching now follows what the lookup resolved. Live:
  `PlanTopology.Phase` no block; `ViewType.Legend` (found enum member) → legend block at the end;
  `Phase` → block; `LabelUtils` unchanged; `apilookup KeepValuesLoadOptions` → the class with "write
  VibeModel.Services.Helpers.KeepValuesLoadOptions"; the NewLabel compile hint carries the new block.
- The recipe's from-the-project route, as worded, run in exec: parameter + label copy + placeholder
  committed in the family, `LoadFamily(doc, new KeepValuesLoadOptions())`, re-opened: `labels=2
  placeholders=1 param=True`.
- **Older bug found on the way (plan 12):** redefining a profile left the project's `VM_Profile` /
  `VM_Source` at their old values (the reload keeps type values), so `extrude` built the old shape
  after "PROFILE UPDATED" (live: 600 x 300 defined, extrude 500 x 250, "reused, not regenerated").
  `profile` now writes both after the reload. Live after the fix: extrude → "updated: regenerated and
  reloaded", 0.18 m², volume 1.80 m³; the same points again → "Unchanged"; the next extrude reuses
  the family.
- Not measured: the `EditFamily` route under change approval `ask` (exec inside a run group).
- Headless: 1518/1518 (the profile fix is Revit-side: `ProfileStore` needs a document; verified live above).

### Review round 2 (2026-10-20 evening) — 9/9 live + profile/extrude/lwall reload paths

- DRY: the stamp rewrite after a reload moved into `GeneratedFamilyStore.Load` (both reload sites
  needed it; one had forgotten it — the profile bug above); one `WriteTypeText` loop for VM_Source
  and VM_Profile.
- The gap block keeps the answer's own line ends (it used to turn the whole answer into `\n` only
  when a gap matched); a one-line answer uses the platform's, like `AppendLine`. Live, the only bare
  `\n` is the one the server puts before its `-- model #` stamp, with or without a gap.
- The label note's manual step now says the user opens the family first (Edit Family) when it was
  prepared from the project.
- Live after the changes: profile → extrude regenerated with the new points, same points
  "Unchanged", next extrude reused; a new `lwall`; the R1–R8 checks of round 1 (recipe keeps its
  work, matching, compile hint). Headless 1521/1521 (`LinkArgsTests.ProbeFile_…` failed once and
  passed on both reruns — flaky, not in this change).

### Review round 3 (2026-10-20, late evening)

- **Found: the recipe's load-back was stamped "1 USER edit since last command - model changed
  outside VibeModel"** (seen in round 1's R1 output, not noticed then). `fd.LoadFamily(doc, …)`
  commits under Revit's own transaction name, and the tracker only trusted the "VibeModel: " prefix.
  Measured: inside a `Transaction(doc, "VibeModel: …")` Revit refuses it ("The document must not be
  modifiable before calling LoadFamily"), inside a `TransactionGroup("VibeModel: …")` it is still
  counted as a user edit — so the recipe cannot fix it. `ModelChangeTracker.RecordCommit` now takes
  `duringVibeModelCommand` (`RevitCommandHandler.IsProcessingCommand`, the flag the dialog and
  failure handlers already use): while a command runs, the main thread is ours. Live after the fix:
  the same load-back → `-- model #1 | …` with no user edit; the recipe on an untouched family
  (`IB_Tag_Rebar_dia-hoh`) → labels 1 → 2, placeholder 1, parameter, clean stamp.
- `ApiLookup.Result` carries `ResolvedType` / `Member`; `KnownApiGaps` no longer reads the JSON
  data keys ("type", "member", "query") to learn what the lookup resolved.
- Live again: profile → extrude regenerate / Unchanged / reuse, a new `lwall`, R2–R8. Headless
  1522/1522 (with `ForeignNamedCommit_DuringOurCommand_IsOurs`); `ProjectMemoryTests.Clear_…` failed
  once while Revit and a build were starting and passed 3 of 3 alone — load-sensitive, not this change.

### Review round 4

- No behaviour change found. `ApiLookup.Result.KnownGap` (set by `Attach`) replaces the last
  dictionary sniff (`Data.ContainsKey("knownGap")` in the compile hint), and the hint now quotes
  exactly the block's lines instead of a 12-line allowance. The `IsProcessingCommand` comment names
  its three users (dialogs, failures, the change tracker). Headless 1522/1522; pure code, covered by
  the fixture-engine hint tests — not re-run live.

### Review round 5

- Docs brought in line with rounds 2–4 (goal line, hook wording, manual step, Files list).
- `Gap` entries are read-only outside the assembly (`internal set`, `IReadOnlyList<string>`): every
  lookup result now holds a reference into the shared static table. No behaviour change.

### Final verify (Revit 2023.1.2, 2026-10-20, branch head 89dd1da) — 18/18 live, 1522/1522 headless

- apilookup: Label (ambiguous + label block: both routes, trap, Edit Family step), FamilyItemFactory.NewLabel
  (block after the first line), TextElement JSON data.knownGap, Phase, ViewType.Legend; no block for
  PlanTopology.Phase, LabelUtils, Wall, TextNote.
- exec doc.FamilyCreate.NewLabel(...) -> the compile hint quotes the whole label block.
- The recipe from the project on IB_Tag_Rebar_aantal-x-dia-hoh: labels 1 -> 2, placeholder, parameter;
  stamp without a USER edit.
- profile redefined -> profile shows 600 x 300, extrude regenerates (1.80 m3), same points Unchanged,
  next extrude reuses; lwall and wall fine. Add-in log: no errors.
