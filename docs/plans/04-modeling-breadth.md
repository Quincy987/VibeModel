# Plan 04 — Modeling Breadth (Expand the Command Vocabulary)

## Goal

VibeModel's value *is* its command surface. Today the entire modeling vocabulary is
**wall, floor, place** (+ `set`/`delete`/`color` edits). This plan expands the creation
primitives so an AI can lay out a building shell, not just three element types.

New `IClaudeCommand`s, prioritized by value/effort, following the existing
`WallCommand`/`FloorCommand`/`PlaceCommand` template exactly: parse mm args → convert via
`RevitUnitHelper.MmToFeet` → wrap in `TransactionHelper.Execute` → return a `... CREATED`
text block. Auto-discovered by `ClaudeCommandRegistry` reflection (no registration).

---

## 0. Key feasibility findings (read first)

- **Easy wins (one-line factory call):** `grid`, `level`. `Grid.Create` / `Level.Create`
  plus the mm→feet conversion already in `RevitUnitHelper`.
- **Easy–medium (factory call + a type/level lookup):** `view` (plan + 3D), `room`, `sheet`.
- **Medium, real dependency:** `tag` — needs a tag family *already loaded* for the element's
  category, and the `IndependentTag.Create` overload changed in 2022.
- **Genuinely hard → DEFER:** `dimension`. The API call (`doc.Create.NewDimension`) is not the
  blocker — producing **stable `Reference` objects Revit will accept** is. References must come
  from geometry computed with `ComputeReferences=true`, not every reference is dimensionable,
  and pairing two parallel references for a linear dimension needs geometric reasoning. Its own
  plan, not a corner of this one.
- **Defer as sugar:** `gridarray` — a loop over `grid`; build it once `grid` proves out.

### Recommended v1 subset
**`grid`, `level`, `view`, `room`, `sheet`, `tag`** (that priority order).
**Deferred:** `dimension`, `gridarray`, section views.

---

## 1. Prioritized table

```
cmd       syntax                        Revit API                        effort  value  v1?
--------- ----------------------------- -------------------------------- ------- ------ -----
grid      grid x1 y1 x2 y2 [name]       Grid.Create(doc, Line)           low     high   yes
level     level elev_mm [name]          Level.Create(doc, elevFeet)      low     high   yes
view      view plan|3d [levelName]      ViewPlan.Create /                low     high   yes
                                        View3D.CreateIsometric
room      room x y [name] [number]      doc.Create.NewRoom(Level, UV)    med     high   yes
sheet     sheet [tbType] [viewId]       ViewSheet.Create +               med     med    yes
                                        Viewport.Create
tag       tag elementId                 IndependentTag.Create (7-arg)    med     med    yes*
dimension dim id1 id2                   doc.Create.NewDimension          HIGH    med    DEFER
gridarray gridarray ...                 loop over Grid.Create            low     low    DEFER
```

\* `tag` is v1 but gated on a loaded tag family for the element's category; it degrades
gracefully with an explicit error when none is loaded.

---

## 2. Per-command detail (v1 subset)

All new modification commands implement `IClaudeCommand, IModificationCommand`, parse args with
the same `Split(new[] { ' ', ',' }, RemoveEmptyEntries)` + `double.TryParse(... InvariantCulture)`
pattern as `WallCommand`, and convert mm→feet with `RevitUnitHelper.MmToFeet`. Failure returns a
string starting with `"ERROR:"` (project-wide convention; relied on by plans 02 & 03).

### 2.1 grid — `Services/Claude/Commands/GridCommand.cs`
- **API:** `Grid.Create(doc, Line.CreateBound(p1, p2))`. Endpoints at `Z = 0`, in feet.
- **Syntax:** `grid <x1> <y1> <x2> <y2> [name]` (mm). Coordinate parse copied from `WallCommand`.
  Optional trailing token → `grid.Name = name`.
- **Transaction:** `TransactionHelper.Execute(doc, "VibeModel: Create Grid", () => { ... })`.
- **Edge cases:** identical start/end → reuse Wall's coincident-point guard
  (`|x1-x2|<0.1 && |y1-y2|<0.1`). Duplicate grid name throws → surfaces as `ERROR:` via the helper.
- **Success output:**
  ```
  GRID CREATED
  ID: <id>
  Name: <name>
  Start: (x1, y1) mm
  End: (x2, y2) mm
  ```

### 2.2 level — `LevelCommand.cs`
- **API:** `Level.Create(doc, RevitUnitHelper.MmToFeet(elevMm))`. Optional `name` → `level.Name`.
- **Syntax:** `level <elevation_mm> [name]`.
- **Transaction:** `"VibeModel: Create Level"`.
- **Note:** creating a level does **not** auto-create its floor-plan view — that is the job of
  `view plan <levelName>`. Document this so the AI chains the two.
- **Edge cases:** duplicate elevation is allowed by Revit; duplicate **name** throws → `ERROR:`.
- **Success output:** `LEVEL CREATED` — `ID:`, `Name:`, `Elevation: <mm>`.

### 2.3 view — `ViewCommand.cs`
- **`view plan [levelName]`:** find a `ViewFamilyType` where `.ViewFamily == ViewFamily.FloorPlan`
  (`FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))`), resolve the level (by name if
  given, else `FormattingHelper.GetPreferredLevel`), then `ViewPlan.Create(doc, vftId, levelId)`.
- **`view 3d`:** `ViewFamilyType` with `.ViewFamily == ViewFamily.ThreeDimensional`, then
  `View3D.CreateIsometric(doc, vftId)`.
- **Section excluded from v1** (needs a bounding-box + transform; low value vs effort — deferred).
- **Transaction:** `"VibeModel: Create View"`.
- **Edge cases:** no matching `ViewFamilyType` in doc; level name not found (list valid level names
  in the error); unknown sub-command → usage string.
- **Success output:** `VIEW CREATED` — `ID:`, `Name:`, `Type:`, `Level:` (plan only).

### 2.4 room — `RoomCommand.cs`
- **API:** `doc.Create.NewRoom(level, new UV(xFeet, yFeet))`. Resolve level via `GetPreferredLevel`.
  Optional `name`/`number` set on `Room.Name` / `Room.Number` after creation.
- **Syntax:** `room <x> <y> [name] [number]` (mm).
- **Transaction:** `"VibeModel: Create Room"`.
- **Edge cases:** point not inside an enclosed region → Revit still places the room but it is
  **unbounded/redundant**; read `Room.Area` and warn when zero/unbounded. Guard for a document
  with no levels. `NewRoom` needs the document to have a phase (always true for projects; guard
  for family docs).
- **Success output:** `ROOM CREATED` — `ID:`, `Name:`, `Number:`, `Level:`, `Area: <m²>`
  (with an explicit "unbounded — not in an enclosed region" note when area is 0).

### 2.5 sheet — `SheetCommand.cs`
- **API:** `ViewSheet.Create(doc, titleBlockTypeId)` — `ElementId.InvalidElementId` for no title
  block, or resolve a `FamilySymbol` of category `OST_TitleBlocks` by name. Optional `viewId` →
  `Viewport.Create(doc, sheet.Id, new ElementId(viewId), centerXYZ)`.
- **Syntax:** `sheet [titleBlockName] [viewId]`. Bare `sheet` creates a blank sheet.
- **Transaction:** `"VibeModel: Create Sheet"`.
- **Edge cases:** `viewId` already placed on another sheet (throws → `ERROR:`); `viewId` is a
  schedule/legend (different placement rules) — catch and report; named title block not found.
- **Success output:** `SHEET CREATED` — `ID:`, `Number:`, `Name:`, and `Placed view: <id>` when a
  viewport was added.

### 2.6 tag — `TagCommand.cs`
- **API (2022+ overload):**
  `IndependentTag.Create(doc, tagSymbolId, activeView.Id, new Reference(element), false, TagOrientation.Horizontal, midpoint)`.
- **Requires:** an active **graphical** view; a tag `FamilySymbol` loaded for the element's
  category (find the first tag symbol whose `Family.FamilyCategory` matches the element's category,
  else the category's default tag type). Activate the symbol if `!IsActive` (then `doc.Regenerate()`).
- **Syntax:** `tag <elementId>` — tags the element in the active view at its bounding-box midpoint.
- **Transaction:** `"VibeModel: Create Tag"`.
- **Edge cases (the main failure mode):** no tag family loaded for the category →
  `ERROR: No tag type loaded for <category>. Load a tag family first.` Also: no active graphical
  view; element id not found; element has no taggable reference.
- **Success output:** `TAG CREATED` — `ID:`, `Tags element: <id>`, `Type:`, `View:`.

---

## 3. Deferred (with rationale)

- **dimension** — `doc.Create.NewDimension(view, line, referenceArray)`. The hard part is the
  `ReferenceArray`: references must be obtained from element geometry computed with
  `Options { ComputeReferences = true }`, not every face/edge yields a dimensionable reference, and
  building a valid linear dimension means selecting two **parallel, opposing** references and a line
  perpendicular to them. This is geometric reasoning, not a wrapper. Defer to a dedicated plan; be
  honest that it is the one genuinely hard primitive here.
- **gridarray** — pure convenience: a loop emitting N `Grid.Create` calls at a spacing. Trivial once
  `grid` exists, but low standalone value. Ship after `grid`; ideally as a `POST /batch` of `grid`
  lines (which, post-plan-02, is already one undo unit) rather than a bespoke command.
- **section views** — folded out of `view` for v1 (bounding-box transform overhead).

---

## 4. Compatibility with plans 02 & 03

### Plan 02 — Transaction Integrity (TransactionGroup) — ✅ no conflict
`TransactionHelper.Execute` opens a plain `Transaction`. Inside an open `TransactionGroup`
(plan 02's batch path), those child transactions commit normally and are merged by
`tg.Assimilate()` into one undo unit. **Requirement met by construction:** every new modification
command uses `TransactionHelper` per-command and is marked `IModificationCommand`, so plan 02's
`IsModification(cmd)` counts them when deciding Assimilate-vs-RollBack. No new command holds a
transaction open across calls or opens its own group — nothing for plan 02 to special-case.

### Plan 03 — I/O Contract (CommandResult / JSON) — migration-ready
v1 commands return `string` via `IClaudeCommand`, so plan 03's adapter auto-wraps them as
`{"ok":true,"text":"..."}` in JSON mode with zero extra work. To make the later migration to
`IStructuredCommand` mechanical, **each success block leads with the created element ID**, so the
structured form is a direct lift:

```csharp
return CommandResult.Ok(text, new Dictionary<string, object> {
    { "created", new List<object> { newElem.Id.IntegerValue } },
    { "name", newElem.Name }
});
```

Failure paths use the `"ERROR:"` prefix, satisfying both plan 03's `WrapLegacyText` and plan 02's
`StartsWith("ERROR")` batch check. Suggested error codes when these migrate later:
`NO_DOCUMENT`, `BAD_ARGS`, `ELEMENT_NOT_FOUND` (tag/sheet by id), `TYPE_NOT_LOADED`
(tag family / view-family-type), `NO_ACTIVE_VIEW` (tag), `TRANSACTION_FAILED`.

---

## 5. Version notes (targets 2022–2025, default 2023)

- `ElementId.IntegerValue` is **deprecated in 2024+** (backing value became `long`) but still
  compiles with a warning. The entire codebase uses `.IntegerValue`; new commands match it for
  consistency — do **not** switch to `.Value`.
- `Level.Create(doc, elevation)` — 2022+ (older API was `doc.Create.NewLevel`; unneeded, min is 2022).
- `IndependentTag.Create` — use the **7-arg overload** (`tagTypeId` first); the older 6-arg overload
  is deprecated since 2022.
- `Grid.Create`, `ViewPlan.Create`, `View3D.CreateIsometric`, `ViewSheet.Create`, `Viewport.Create`,
  `doc.Create.NewRoom(Level, UV)` — stable across 2022–2025; no conditional compilation needed.

---

## 6. Verification approach (Revit running)

Build & restart via the project script (never manual):
```powershell
.\build.ps1 -Debug
```
With a document open, curl each new command and **visually confirm the geometry appears**:
```bash
curl -s http://localhost:18884/health
curl -s "http://localhost:18884/grid?args=0 0 10000 0 A"      # grid line A appears in plan
curl -s "http://localhost:18884/level?args=3000 L2"           # L2 appears in a section/elevation
curl -s "http://localhost:18884/view?args=plan L2"            # new floor plan in Project Browser
curl -s "http://localhost:18884/view?args=3d"                 # new 3D view
curl -s "http://localhost:18884/room?args=2500 2500 Office 101"  # room + area (enclose first)
curl -s "http://localhost:18884/sheet?args"                   # blank sheet in Project Browser
curl -s "http://localhost:18884/select?args=<wallId>" && curl -s "http://localhost:18884/tag?args=<wallId>"
```
Per-command checks:
- **grid/level:** returned `ID:` matches a new datum; press **Ctrl+Z once** removes it cleanly.
- **view:** new view node in the browser at the right level.
- **room:** `Area` non-zero when placed inside walls; "unbounded" note when not.
- **sheet:** sheet appears; if a `viewId` was passed, the viewport lands on it.
- **tag:** tag visible in the active view; clear `ERROR: No tag type loaded...` when none is loaded.
- **batch + plan 02:** send several `grid` lines as one `POST /batch` → one undo entry.

Check `%LOCALAPPDATA%\VibeModel\logs\` for transaction/API errors after each test.

---

## Summary of decisions

- **v1 subset:** `grid`, `level`, `view` (plan + 3D), `room`, `sheet`, `tag` — all thin wrappers
  over stable factory APIs, following the `WallCommand` template.
- **Deferred:** `dimension` (stable-reference problem is genuinely hard — own plan), `gridarray`
  (sugar over `grid`/batch), section views.
- **Plan 02:** new commands assimilate into the batch `TransactionGroup` automatically by using
  `TransactionHelper` + the `IModificationCommand` marker. No special-casing.
- **Plan 03:** v1 stays string-returning (auto-wrapped); success blocks lead with the created ID so
  the `IStructuredCommand` migration is a direct lift.
- **Version safety:** match existing `.IntegerValue`; use the 2022+ 7-arg `IndependentTag.Create`;
  all other APIs stable 2022–2025.
