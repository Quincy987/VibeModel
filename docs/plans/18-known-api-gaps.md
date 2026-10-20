# Plan 18 — Known API gaps: say what Revit's API cannot do, at the moment it matters

> Goal: when the AI runs into something the Revit API genuinely cannot do — a label in a tag,
> generic-annotation or title-block family, a legend view, a design option, a phase — VibeModel
> names the gap and the workaround that was measured, in the answer the AI is already reading
> (`apilookup`, and the hint under a failed `exec` compile). No per-turn prompt text.

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
    must not be open); the family document takes its own `Transaction`; `fd.Close(false)` after.
- **The step that stays manual**: select the copied label → *Edit Label* → pick the parameter
  (one dialog per label), then Load into Project. Everything before and after it is API work.
- **Also no API** (checked by reflection on RevitAPI.dll 2023): `DesignOption` and `Phase` have no
  creation member; legend views have no class and no creation call (plan 13 documents the
  "duplicate an existing legend" route). Both worth the same treatment — the model keeps trying
  `Phase.Create` until it reads that nobody can.

## Decisions

- **Not a prompt rule.** It would cost every turn for a case that is rare, the colleague's own
  failure was not retries but surprise, and a blunt "labels are not possible" invites the model to
  stop before the parameters and types it *can* make. The knowledge goes where it is read on demand.
- **One pure table, `Services/Claude/KnownApiGaps.cs`**: entries `{ Keys, Title, Cannot, Works,
  Manual }`. Matching is case-insensitive on the simple name and on the member after the last dot,
  so `Label`, `Labels`, `NewLabel`, `FamilyItemFactory.NewLabel`, `TAG_TAG`, `TextElement`,
  `LabelType` → the label gap; `Legend`, `ViewLegend`, `LegendComponent`, `ViewType.Legend` → the
  legend gap; `DesignOption`, `DesignOption.Create`, `Phase.Create`, `NewPhase` → the
  design-option/phase gap (`Phase` alone is *found* and useful; only its creation is a gap, so the
  bare type name matches but the note is worded "exists; cannot be created").
- **Hook 1 — `apilookup`** (`ApiLookup.Lookup` result): after *not found*, *ambiguous* and *has no
  public member* a `Known gap:` block; after a *found* type that is a key (`TextElement`,
  `DesignOption`, `Phase`) the same block under the member list. Text: title, one line "cannot",
  the measured "what works" lines (code fragments as above), the manual step. ≤ 12 lines. JSON:
  additive `knownGap: { name, cannot, works: [], manual }`.
- **Hook 2 — exec compile hint** (`ApiLookupCommand.CompileErrorHint`) already prints the first
  20 lines of the lookup, so `doc.FamilyCreate.NewLabel(…)` (CS1061) gets the gap for free. Make
  sure the block is inside those 20 lines (put it before the closest-names list when the query is
  a gap key).
- **Hook 3 — the silent-success trap**: an exec snippet that calls `Set` on a parameter whose
  `StorageType` is `None` gets no error from Revit. Not interceptable in general; the gap note
  says it. (A runtime check would mean wrapping every `Parameter.Set` — out of scope.)
- `apilookup`'s `Description` mentions it ("…and what this API cannot do: labels in annotation
  families, legends, design options, phases"), so the model has a reason to ask.
- **Not doing**: a `label` command (commands run on the project document; the family editor
  session is the user's; one report so far) and UI automation of the Edit Label dialog (fragile,
  the first thing a Revit update breaks). Revisit if the placeholder route is reported as too slow.

## Files

- `src/VibeModel/Services/Claude/KnownApiGaps.cs` — new, pure: the table, `Match(string query)`,
  `Describe(entry)` text + data.
- `src/VibeModel/Services/Claude/ApiLookup.cs` — `NotFound`, the "has no public member" result
  and `DescribeType` append the block; `Result.Data["knownGap"]`.
- `src/VibeModel/Services/Claude/Commands/ApiLookupCommand.cs` — `Description`; the compile hint
  keeps the block within its 20 lines.
- `src/VibeModel.Tests/KnownApiGapsTests.cs` — matching (keys, case, dotted, non-matches such as
  `LabelUtils`? — decide: `LabelUtils` is a real, useful type; it must NOT match), text budget, JSON.
- `src/VibeModel.Tests/ApiLookupTests.cs` — the three result kinds carry the block; a found type
  that is not a key does not.
- `CLAUDE.md` (apilookup bullet), `CHANGELOG.md`, this plan (verification section after the live run).

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
