# Small gaps after the v1.2.8 integration — research + fixes (2026-10-13)

Base: `development` b716df4 (v1.2.8). Branch: `claude-bot/research-smallgaps`.
Tests: 1004 green before, 1017 green after (`dotnet test VibeModel.sln -p:RevitVersion=2023`).

Live checks ran in a **separate Revit 2023 instance** (port 18886) on a copy of
`demo_out/plan12/prg1002_sheet_test.rvt` (never saved). `build.ps1` was NOT run: it closes every
Revit process, and at the time the user's instance had an unsaved model open and three other
sessions were booting their own instances. The new code was loaded next to the deployed add-in
with `Assembly.LoadFile` inside `exec`; the shared add-in folder still holds the build that was
there before (12 Oct 22:35), not this branch.

## 1. "The IFC export contains no piles" — they were there, as IfcSlab

**Cause (Revit's default, not ours).** The piles are exported. Revit 2023's default IFC class table
`C:\ProgramData\Autodesk\RVT 2023\exportlayers-ifc-IAI.txt` contains
`Structural Foundations → IfcSlab BASESLAB`, and the pile family (`NLI_Funderingspaal hout`) has no
override (`Export to IFC As`, `Export Type to IFC As`, predefined type: all empty; `Export to IFC` =
By Type / Default). So every pile arrives as `IFCSLAB(… .BASESLAB.)`; a search for
`IFCPILE` / `IFCFOOTING` / `IFCCOLUMN` finds nothing. Not the options, not the view's filters, not
the phase.

Measured on the copy (whole model, deployed build, before the change): 510 pile instances
(`Ø240` 390 + `Ø200` 120) + 64 sheet-pile elements = 574 `IfcSlab.BASESLAB`, 0 IfcPile/IfcFooting/IfcColumn.
The sheets2 file `prg1002_sheets2 - Detail palen 1-50.ifc` holds 36 piles the same way.

**Change.** `export ifc` now reads its own file back (`IfcContents`, Revit-free, unit-tested):

```
IFC contents (read back from the file): 790 Revit elements (1004 IFC entities) — 790 of the 793 model elements in scope
  Structural Foundations    574 → IfcSlab.BASESLAB
  Generic Models            119 → IfcBuildingElementProxy
  Walls                      63 → IfcWallStandardCase ×56, IfcWall ×7
  Structural Framing         20 → IfcBeam
  Floors                     14 → IfcSlab.FLOOR ×7, IfcSlab ×7
Not in the file: Site — none of 2, Generic Models 1 of 120
  A category with none exported is usually mapped to 'Not Exported' … or 'Export to IFC' = Don't Export; a whole-model export also skips …
  Note: 574 Structural Foundation family instances were exported as IfcSlab (Revit's default IFC class for that category) — piles are in the file, but not as IfcPile/IfcFooting. To change that, set the type parameter 'Export Type to IFC As' (e.g. IfcPile, predefined DRIVEN) or remap the category in the IFC export classes.
```

Verified live for the whole model, a 3D view (`3D View algemeen`, 790 of 792) and a section
(`Doorsnede bij trap`, 20 of 20). Counts are Revit elements, not IFC entities (shape-edited floors
arrive as up to 83 IfcSlab pieces with the same id). Scope = the view's visible model elements, or
every non-demolished model element; sketches, sheets, base points, openings, reveals, rooms, links
and imports are left out. The JSON result gets an additive `ifc` object (`elements`, `byCategory`,
`inScope`, `missing`). The IFC class table has no Revit 2023 API, so a category mapped to
"Not Exported" is inferred (none of its elements in the file), not read.

**Not changed, worth knowing.** A larger IFC export takes 15–30 s; two in one `exec` exceeded the
HTTP 30 s wait (Revit finished them anyway).

## 2. Answers of 6–8 lines instead of 5

**What the extra lines were** (all 65 answers of the 12 Oct chats in `%LOCALAPPDATA%\VibeModel\chats\`, final text only):
- **Next-step offers** — almost every answer closed with a 2–3 item list ("Mogelijke volgende
  stappen: 1. … 2. …", "Wil je dat ik: 1. 2. 3."). Two prompt lines asked for it:
  `AppendCommonBehavior` ("may end with 1-2 logical next steps") and the Claude Code turn prompt
  ("end with at most 2 specific suggestions").
- **The phase-1 view** — re-explained in 7 of the 9 wall create/colour/delete tests and in 4
  other answers (section/phase questions, model overview, pile placement), driven by the TEMPLATE block's `! this view is on the EXISTING phase`
  line, which is there every turn whether or not anything was created.
- Smaller: "Lengtes boven 10 000 mm staan in m" (the units rule said "say so once"), "Elke stap is
  een eigen undo-stap", the check WARN about empty BIM-ID fields, and the ids line.

**Change (prompt only).** Removed both next-step nudges; the RULES' compact block now says the 5
lines count everything except a table and the id line, allows ONE closing offer line (kept on
purpose: the quick-reply prompt only offers chips when the answer offered or asked something),
mentions the view's phase only when this turn's work is hidden by it, and drops undo/unit
restatements. The TEMPLATE warning line says the same.

**A/B** — the exact logged turn prompt of 12 Oct 19:55 replayed through `claude -p` with the pane's
flags against the copy, old vs new rules (only the rule lines differ), 2 runs each, Opus 5.5.
Lines counted as the new rule counts them (non-blank, excluding table rows and the final id line):

```
prompt                                              old (run1, run2)   new (run1, run2)
geef een tabel van de 5 langste wanden …            5, 5               2, 3
maak een wand … kleur hem rood en verwijder …       7, 5               5, 5
welke fase hoort een nieuwe doorsnede …leidraad?    6, 6               3, 4
mean                                                5.7                3.7
```

New answers end with exactly one offer line in 6/6 runs (old: a 2-line list in the table answers).
The wall answer still names the phase-1 view — correctly, the new wall was hidden by it — and once
still mentioned Ctrl+Z.

## 3. README / CLAUDE.md consistency

- All 78 commands: 68 had a CLAUDE.md example; **10 had none** (`filter`, `measure`, `whereused`,
  `zoomto`, `isolate`, `unisolate`, `hide`, `show`, `colorsplash`, `chatlog`) → new "View & Analysis
  Commands" block, each example run live on the copy.
- Every read-only CLAUDE.md example (52) run against the copy: all parse; failures are only
  placeholder ids ("not found") and documents that are not in the copy's folder.
- `StatusVerbs`: the existing nl/en parity test passes, but `chatlog` was in neither map (the test
  only compared the two maps). Added both labels + a test that every registered command has one.
- README "Things you can say" / welcome examples tried as single turns: "Wat zit er in dit model?"
  (one turn, 5 counted lines + table) and "Waarom zie ik element 1728711 niet in deze view?" after
  hiding it (one turn, names Hide in View, 4 lines). Side note: the inventory answer created three
  temporary schedules to count and deleted them again (3 journal lines) — a read question that
  briefly modifies the model.
- `whereused <family> <type>` only finds loadable families (a wall type by name is "not found"; the
  type id works) — documented as such. It lists walls under "(no level)"; not fixed here.
