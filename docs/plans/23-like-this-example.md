# Plan 23 — "do it like this example" (phase 3)

Follows v1.2.23 (phase 2). Source: a colleague's 45-chat export (kept out of git). In every chat
with a command record the request points at an example already in the model ("like the other
side", "finish like X", "reproduce X"). The same three steps failed each time:

1. **finding** the example set by a value — a script looping over every parameter of every element
   crashed Revit twice (a protected-memory error);
2. **reading** it relative to its reference line, and **looking** closely at a detail — the
   assistant built temporary views by hand (12 steps) or cropped PNGs itself into a folder the next
   call could not read (3 failed reads);
3. **reproducing** it without damaging neighbours — copies lost a spot elevation on a neighbour.

And the claim "same as your example" was followed by a correction in about 4 of 9 such requests:
nothing outside the model compares the result with the example.

Three releases, each its own build → review → verify chain:

- **3a (this release)**: find and look, read-only — `filter any`, `screenshot --at/--box`, plus
  three small fixes.
- **3b**: one read-only `example` command that describes a set relative to a reference line
  (position along it, offset to the side, base/top, type, length, phase, the parameters that vary
  inside the set, joins, hosts) and, with `--vs <set>`, compares two sets pair by pair (WARN above
  5 mm, unpaired elements named, one `Verdict:`), plus the rule "on *zoals / net als*, end with that
  compare and fix or report each difference". Built before 3c: the false "same as" came mostly from
  the assistant's own builds, not from copies.
- **3c**: `copy <ids> --by dx dy [dz] | --mirror <line>` in one undo step. Needs first: a live
  measurement of mirroring (family hand/facing, walls' exterior side, framing start/end), hosted and
  grouped elements, the phase/workset/design option a copy takes, pinned sources; the annotation
  guard adapted for copies (it assumes a replacement — it must never move a neighbour's spot onto a
  copy, and must look at the neighbours a copy's join recipes touch); a pairing of source → copy
  (Revit's copy returns no mapping). Open question for the owner: do copies get a new Mark?

## 3a.1 `filter any <param> <op> <value>`

`any` is caught before the category is resolved (its substring fallback must never make it a real
category). One collector over the whole model (or `--view`), not element types, only elements with
a category, minus the parts nobody calls an element (`ApprovalRun.Hidden`: sketch lines, automatic
sketch dimensions, a new view's camera/viewer/section box, preview legend components, viewports on
no sheet). Each element is read for ONE parameter: `LookupParameter(name)`, else its type's (cached
per type id) — never an enumeration of `elem.Parameters` per element. Revit's lookup is
case-sensitive: when nobody carries the name as typed, the spelling is taken once from a sample
(the first element of each category and of each type, ≤ 300, read whole) — `comments` →
`Parameter: 'Comments' (as Revit spells it)`; still none → `ERROR: no element has a parameter
'Lenght'` + the closest names. The comparison is the one the per-category path always used, now in
the pure `FilterQuery` (text/name: `=`/`!=`/`contains`, case-insensitive; numbers in the unit
`params` prints, `=` within half a unit; integers, else the value Revit shows). The per-category
path keeps `FindParameter` and its exact behaviour.

Output: per category its count (largest first), then ≤ 50 elements with category, name, type, Mark
and the parameter's value; `--ids`, `--select`, `--count <param>` (without a filter: only the
elements that carry it; a blank value counts as `(empty)`). Above 500 ms a WARN is logged.

Measured (Revit 2023, demo model, 2 803 elements read, 2026-11-27): `filter any "Type Name"
contains AZ` → 60 in 1 category, 125 ms; `filter any Length > 20000` → 22 in 4 categories, 99 ms;
`filter any --count Mark` 145 ms; `comments = x` (spelling resolved from the sample) 177 ms;
a missing name 117–125 ms. About 50 µs per element: a model of 100 000 elements would take ~5 s.

## 3a.2 `screenshot --at <ids|selected> [--margin mm] | --box x0 y0 x1 y1`

A close-up at full resolution of part of a view without changing it — also a view on a sheet,
which the assistant may not change. `--box` is in the view's own frame (plan: project x,y mm;
section/elevation: along/elevation mm, as `crop --box` and `text`); `--at` takes the elements' box
(`CropCommand.FitExtent`) + `--margin` (500 mm). With `--view <sheet>`: `--at` zooms the first
view on the sheet that shows the elements and can be cropped (a perspective view cannot); `--box`
is then paper mm and maps through the viewport (`Viewport.GetProjectionToSheetTransform`, the
view's `GetModelToProjectionTransforms`), all four corners — on a plan turned against the project
axes the world box of two corners is larger; measured: a 60 × 40 mm paper box at 1:500 crops
exactly 30 × 20 m — and a box inside no single viewport is refused with the reason
(`ZoomArgs.PickViewport`, pure, smallest containing viewport wins). `--at` on a view that does not
draw the elements adds a `Note:` with `whyhidden`; ids that do not exist are counted.

Mechanism: a `TransactionGroup` that is always rolled back. One transaction duplicates the view
(`WithDetailing`, else `Duplicate`, said so), lets go of a scope box, crops it (`CropCommand.CropTo`;
a 3D view also gets a section box), hides the crop frame, turns the annotation crop on at Revit's
smallest offset and hides view markers (sections, elevations, callouts) that are not wholly inside
the zone — then `ExportImage` of the copy with no transaction open, then the rollback. While it
runs a `TemporaryWork` scope makes `OnDocumentChanged` return early: no edit counted, nothing in the
undo ledger, the change recorder or "created by VibeModel"; the edit counter is rewound as well
should anything have moved it. The response keeps the `Path:` line and adds
`Zoom: <box in the view frame>, <w> × <h> mm (<source>), on a temporary copy of the view … (nothing changed)`.

Measured (Revit 2023, demo model, 2026-11-27):
- `ExportImage` of the copy inside the open group works, and the rollback removes it: the view
  count stayed 52 and the stamp's counter did not move, on a plan with a view template, a plan, a
  section, a section on a sheet (paper box), a 3D view and via the sheet with `--at`;
- a 4 000 × 3 000 mm zone of a 1:50 section at 1 536 px (384 px per m): every dimension text,
  level tag and note legible;
- a plan copy without the annotation crop exported mostly far-off notes (1 536 × 315 px, the
  element a speck); with the annotation crop, a far section marker still widened the image — both
  fixed as above, after which the zone fills the image;
- after `wall …`, a zoom, then `undo`: the wall was taken back (the zoom left no undo entry and did
  not trip the ledger's counter check); an atomic batch `wall` + zoom reported `-- changed: added 1
  (Walls 1)` only; a preview batch and `screenshot …&preview=1` worked; under *Alleen lezen* and
  *Alleen opmaak* the zoom worked while a `wall` was refused;
- 280–540 ms per zoom (the plan with a point cloud and a template the slowest).

## 3a.3 Small fixes

- `sheetinfo` prints each viewport's detail number (`#5`, JSON `detailNumber`) and how to change it.
  Measured: a viewport carries two parameters called "Detail Number" and the first one Revit lists
  is read-only, so `set <viewport> "Detail Number" 3` was refused — `ParamWriter.Lookup` now prefers
  a writable parameter when several share a name (also for `place --set` and the profile commands).
- `region` (filled region and detail component) prints where the view sits in its frame
  (`View in this frame: x 15343…24192, y -2525…3101 mm (crop region)`, `SheetHelper.FrameExtent`),
  so a region placed from a script's own origin no longer lands shifted.
- Known fact `terrain` (`KnownApiGaps`, on `TopographySurface`/`Toposolid`): Revit 2023 has no
  Toposolid; `TopographySurface.Create(doc, points)` triangulates the convex hull, `Create(doc,
  points, facets)` keeps a concave outline. Measured: an L of 300 m² from 8 points → 350 m² from the
  points alone, 300 m² with its 6 facets (`IsValidFaceSet` true).

## 3a.4 Rule

`ChatBackendBase.FindAndLookRule` (Complete and checkable): find by a value with `filter any`, never
an exec loop over every parameter; check a detail with `screenshot --at <ids>` / `--box` (with
`--view` when the user names a view), never a temporary view or a self-cropped PNG.

Measured in the chat pane (Claude Code, 2026-11-27): "zoek in het hele model alles waarvan de
typenaam AZ 24-700 bevat, en laat damwand 1619322 van dichtbij zien in het bovenaanzicht" →
`filter any "Type Name" contains "AZ 24-700" --count Category` + `screenshot --at 1619322 --margin
2000`, 35 s, nothing changed. It zoomed the active plan rather than the view called "Bovenaanzicht"
("bovenaanzicht" is also just "plan view").

## Not done in 3a

- Models much larger than the demo (2 803 elements) were not measured for `filter any`.
- A view Revit cannot duplicate is refused with a hint (whole-view screenshot); not met live.
