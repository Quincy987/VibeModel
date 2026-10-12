# Plan 13 — Legends, Keynotes and Dimensions in 3D Views

> Goal: close (or honestly re-label) the three annotation gaps the README still marks ❌ —
> **legends** (§5 "the Revit API can't create them"), **keynotes** and **dimensions in 3D views**
> (§6) — with designs that rest on what the Revit 2023 API was *measured* to do, not on forum lore.

> **STATUS (research, nothing built).** Every claim below comes from probes run through `exec`
> on Revit 2023.1 (23.1.20.70) with the v1.2.8 add-in (b716df4), on three documents: a copy of
> the PRG1002 sheet-test model (built on the pilot office's template; never saved), a fresh
> project from Autodesk's `DefaultMetric.rte`, and — for legend components — Autodesk's sample
> project `BIM_Projekt_Golden_Nugget-Architektur_und_Ingenieurbau.rvt`. The office template
> itself was opened read-only for the keynote/legend inventory. Probe scripts and the full log:
> `.verify/annotation/` (gitignored; `p1_*` … `p6_*`, `probe_log.txt`, `img/`).
> Prior attempts: none — plan 12 listed legends as out of scope ("legend views cannot be created
> through the Revit API"); no legend/keynote/3D-dimension code exists.

## TL;DR

| | Verdict | Why (measured) | Build? |
|---|---|---|---|
| Legends | 🟡 → ✅ with a seed | No legend factory exists, but `View.Duplicate` on any legend works, and everything a legend holds can be added or copied in — legend components only by **copying an existing one** and retargeting its type | yes — `legend`, size M |
| Keynotes | ❌ for the tag, 🟡 for the data | `IndependentTag.Create` with a keynote tag type makes a tag with **no key source**: it shows `?`/empty, its Key Source is read-only, a keynote legend lists nothing. Autodesk documents keynote tags as not creatable. Keynote *values* (type/material) and the table work | small — `keynotes` + `keynote` (values only), size S |
| 3D dimensions | ✅ | `NewDimension` works in a 3D view (locked, unlocked, even perspective) once the view's **work plane contains the dimension direction**; the result draws in the view and on a sheet | yes — `dimension --view <3d>`, size M |

---

## 1. Legends

### 1.1 What the API offers (probe `p1_inventory.cs`)

- Public factories on `View`/`ViewDrafting`/`ViewSheet`: `View.Duplicate`, `ViewDrafting.Create`,
  `ViewSheet.Create`/`CreatePlaceholder`. **No** `Legend.Create`, no `Creation.Document.*Legend*`,
  no public `LegendComponent` class (`ViewFamily.Legend` exists only as a view-family-type value).
  Confirmed: a legend can never be made from nothing.
- PRG1002 (from the office template) holds four legends, all at **1:1**, none with a legend
  component: a materials legend (5 texts, 4 filled regions, 5 detail lines, 4 reference planes,
  on sheet 0001), a site legend, a steel legend (34 lines, 26 regions, 14 dimensions) and a
  pile-symbol legend (110 lines, 27 arcs, 48 regions). The template itself has the same four,
  and **0 legend components** anywhere. A fresh `DefaultMetric` project has no legend at all.

### 1.2 Duplicate and fill (probe `p2_legend.cs`) — all measured OK

| Operation | Result |
|---|---|
| `Duplicate(ViewDuplicateOption.Duplicate)` | new legend, **empty** (only its extents element) |
| `Duplicate(WithDetailing)` | full copy (lines, texts, regions, reference planes) |
| `Duplicate(AsDependent)` | refused: `InvalidOperationException: View cannot be duplicated` |
| rename (`View.Name`), `View.Scale = 20` | OK |
| `TextNote.Create`, `FilledRegion.Create`, `NewDetailCurve` | OK |
| `NewFamilyInstance(pt, symbol, legend)` with a detail item / generic annotation | OK |
| same with a model family (pile) | refused: "FamilyPlacementType is not ViewBased" |
| `Viewport.Create` on a 2nd sheet | OK — `CanAddViewToSheet` is true on another sheet, false on the same sheet |

The legend frame is the XY plane (origin 0, right = X, up = Y, view direction = Z, no sketch
plane). The materials legend's swatches are 20 × 7 mm filled regions with 2.5 mm text, rows
13 mm apart — drawn in paper millimetres because the legend is 1:1.

The existing `viewport` command already places a legend on a second sheet (measured: the steel
legend on 0001 *and* a probe sheet). One wording bug: when a legend is already on the *same*
sheet, the refusal says "a view can be on one sheet only", which is wrong for legends.

### 1.3 Legend components — copy and retarget (probes `p3*_*.cs`)

- **Cross-document copy works.** `ElementTransformUtils.CopyElements(sourceLegend, ids, targetLegend, …)`
  copied a legend component from the Autodesk sample into PRG1002. Copying within a legend
  (`CopyElement`) works too.
- **Whole legend views copy across documents.** `CopyElements(srcDoc, {legendId}, dstDoc, …)`
  brought both a PRG1002 legend and a 6-component sample legend into the fresh project; the
  components' wall types came along. Reference planes were not copied.
- **Blocker to handle:** when a copied type already exists but differs, Revit raises the modal
  **"Duplicate Types"** dialog and the external event hangs (the HTTP call timed out at 30 s
  until the dialog was clicked). The command must set
  `CopyPasteOptions.SetDuplicateTypeNamesHandler(UseDestinationTypes)`.
- **Retarget = set `LEGEND_COMPONENT`.** Measured on one copied component: wall type ✅,
  floor type ✅ (view switched to Section by itself), pile and sheet-pile family types ✅, generic
  model ✅, detail item ✅. **Revit also accepts a generic annotation, a text type and a material
  without complaint** — the parameter takes any element id. The command must whitelist
  categories itself.
- `LEGEND_COMPONENT_VIEW` takes −9 Elevation Right, −8 Floor Plan, −7 Elevation Front,
  −6 Elevation Back, −5 Section, −4 Ceiling Plan, −3 3D (other values set but show blank).
  `LEGEND_COMPONENT_LENGTH` is writable for system families (wall, floor) and **read-only** for
  loadable families.
- **Scale matters.** Components draw at model size ÷ legend scale. In a 1:1 legend a pile
  family drew 2.7 × 11 m. In an empty duplicate set to 1:50 (with length = 30 mm on paper ×
  scale) the result on an A0 sheet was right: wall plan and floor section swatches 30 × 6 mm,
  pile plan symbol 55 × 16 mm, pile elevation 55 × 221 mm, sheet-pile plan 37 × 10 mm
  (`img`: the `LEG-50` sheet). Filled-region swatches do *not* rescale, so a seed's rows must be
  redrawn at the target scale (paper mm × scale).

### 1.4 Verdict

> **Decision (2026-10-13):** the office template's own legends are the seed — `legend` duplicates
> `Legenda_materialen` (or any named legend) and adds swatch/symbol rows. **No seed `.rvt`, no
> legend components (`type:` rows) in v1** — low priority; the component path above is kept as
> the documented route if type legends are ever wanted.


🟡 → ✅ **when a seed exists.** Everything needed is reachable through the API except creating
the first legend view and the first legend component. Both can come from:

1. **a legend already in the document** (the office template ships four), for the view; and
2. **a seed `.rvt` in the standards pack** holding one legend with one component, for
   components. The office template has no component, so without a seed the command can still
   make swatch/symbol legends but not type legends.

### 1.5 Command design

```
legend --list                                          # legends, scale, sheets, row count
legend <new name> --from <legend|seed> [--scale n] [--keep]
       [--row "fill:<filled region type>=<text>"]      # swatch, like the office materials legend
       [--row "type:<wall/floor/family type>[@plan|section|front|back|right|3d]=<text>"]
       [--row "symbol:<detail item | generic annotation type>=<text>"]
       [--sheet <number> --at x y]
legend <existing name> --add-row "…"                    # append rows to a legend
```

- `--from` names a legend in the document, or `seed` (the pack's seed legend, copied in once
  per document). Default is an **empty** duplicate. With `--keep` it's a duplicate with
  detailing, and rows are appended **below** the existing content's bounding box.
- Row layout is Revit-free (`LegendLayout`): swatch column at x = 0, text column at
  swatch width + 5 mm, row pitch max(swatch height, text height) + 6 mm, all in paper mm ×
  scale. With `--keep` the pitch and swatch size are read from the seed's own rows (measured:
  20 × 7 mm, 13 mm pitch).
- `type:` rows copy the document's (or seed's) component, set `LEGEND_COMPONENT` after checking
  the target is a wall/floor/roof/ceiling/stair type or a model-family `FamilySymbol`
  (loaded first via the existing `loadfamily` path), set the view value, and set the length
  only when writable. They re-read the bounding box and refuse when it didn't change.
- Every type that is not in the document yet (`fill:`/`symbol:`) gets the usual
  "unknown → list candidates" error; nothing is half-built (one transaction, one Ctrl+Z).
- `--sheet` reuses `viewport`'s placement.
- Response: the legend id, the scale, each row with its kind, its type and its paper size
  (the way `viewport` reports), and the sheets it is on.

### 1.6 Files touched

- `Services/Claude/Commands/LegendCommand.cs` (new, `IModificationCommand, IStructuredCommand`).
- `Services/Claude/LegendArgs.cs` (new, Revit-free parsing, like `SheetArgs.cs`).
- `Services/Helpers/LegendLayout.cs` (new, Revit-free row math).
- `Services/Helpers/SheetHelper.cs`: legend lookup by name/id.
- `Services/Claude/Commands/ViewportCommand.cs`: same-sheet wording for legends.
- Standards pack: an optional `legends: { "seed": "seed/legends.rvt", "defaultScale": 50 }` in
  `pack.json` plus loader and validation (`StandardsPack`); `standards/README.md`;
  `standards/example-office/` gets a tiny seed file.
- `Services/Chat/StatusVerbs.cs`: both maps (a test enforces it).
- `CLAUDE.md` (Sheet Assembly section) and the README status line.

### 1.7 Risks

- **Modal dialogs**: Duplicate Types (measured) and possibly "elements were not copied" —
  always install a duplicate-types handler and the warning swallower.
- **Unvalidated parameter**: `LEGEND_COMPONENT` accepts garbage silently — whitelist +
  bounding-box check.
- **Opening a seed `.rvt`** costs seconds and, from a network pack, must go through the
  plan-11 mirror (never read a share on the main thread). Close it after copying; cache
  nothing across documents.
- **Scale mismatch**: rows copied from a 1:1 seed into a 1:50 legend shrink by 50 — layout
  always in paper mm × target scale; `--keep` keeps the seed's scale.
- **Legend export**: `screenshot --view <legend>` refuses ("View type 'Legend' cannot be
  exported"), so verification goes through a sheet (`screenshot --view <sheet>`), as the probes did.

### 1.8 Tests and size

- Headless: `LegendArgsTests` (row kinds, `@view` suffixes, quoting, conflicting flags) and
  `LegendLayoutTests` (pitch, columns, scale conversion, `--keep` below existing content);
  `StandardsPackTests` for the `legends` block.
- Live (`.verify`): PRG1002 copy — `legend Test --from <materials legend> --keep --row fill:…`;
  `legend Palen --from seed --scale 50 --row type:"Betonpaal 320x320"@plan=… --row type:…@front=…`;
  place it on two sheets; screenshot both sheets; fresh `DefaultMetric` project — `--from seed`
  with no legend in the document.
- Size **M**: about 4 new files plus 5 touched, ~600–800 lines including tests.

---

## 2. Keynotes

### 2.1 Measured (probes `p4*_*.cs`)

- **Table access works.** `KeynoteTable.GetKeynoteTable(doc).GetKeyBasedTreeEntries()` →
  3840 `KeynoteEntry` items (key, parent, text) in PRG1002, the office template *and* the
  fresh project — even though the referenced file is **not on disk** (`GetLinkedFileStatus() =
  NotFound`). The model keeps a cached copy of the last load.
- **What the template carries**: a reference to Autodesk's default `RevitKeynotes_Metric.txt`
  (US MasterFormat, English, `PathType.Content`) — not an office file. No element type and no
  material has a keynote. The office keynote tag family is loaded (types Text / Number /
  Number_boxed), and there is no keynote legend. **The office does not use keynotes today.**
- **Values**: the type parameter `KEYNOTE_PARAM` (element keynote) and the material's
  `KEYNOTE_PARAM` (material keynote) are writable. **Any string is accepted**, including
  `NOT-A-KEY` — the command must validate against the table.
- **Loading a file**: `KeynoteTable.LoadFrom(ExternalResourceReference.CreateLocalResource(…, KeynoteTable, path, Absolute), results)`
  works. Two traps: a file with **LF line endings fails on every line** ("syntax errors", the
  reference file is UTF‑16LE with CRLF), and that failed load still **returns `Success` and
  empties the table** (0 entries). After the CRLF fix: 4 entries, status Loaded.
- **The tag**: `IndependentTag.Create(doc, keynoteTagTypeId, view, new Reference(wall), …)`
  creates an element in the Keynote Tags category with an empty `TagText`, an empty, **read-only**
  `KEY_SOURCE_PARAM` and an empty `KEYNOTE_TEXT`. This holds for all three tag types, in plan and
  in section, before and after loading a valid file, with the wall type's keynote set.
  `KEY_VALUE` is writable but setting it changes nothing. A control tag (multi-category,
  `TypeName`) placed the same way reads `Beton_Metselwerk_650` correctly. A keynote legend
  (`ViewSchedule.CreateKeynoteLegend`) is created but lists nothing. Autodesk's API guide says
  keynote tags' "specific features are not exposed by the API and cannot be created using the
  NewTag() method".
- Face references for material tags/keynotes were refused ("The reference can not be tagged")
  for every planar face of the wall, in plan and in section — `TM_ADDBY_MATERIAL` with an
  ordinary material tag too. Not resolved.

### 2.2 Verdict

❌ for placing a keynote **tag** through the API (documented and measured).
🟡 for keynote **data**: listing, validating, loading and assigning keys is fully possible.

### 2.3 Command design (values only)

```
keynotes [filter] [--tree]       # table: file path + status, entry count, matching key | text
keynotes --load <path>           # (re)load a file; refuses when the result has 0 entries
keynote <id…> <key> [--material] # set the type (or the element's first-layer/selected material) keynote
```

- **Missing file**: `keynotes` still lists the cached entries with one warning line:
  `file not found: <path> — showing the copy stored in the model (N entries, may be stale)`.
- `keynote` refuses an unknown key and lists the closest matches. It warns that a type keynote
  applies to **all** N instances of that type, and names the type. With `--material` it names
  the material.
- `--load` runs in a transaction and checks the entry count before committing. A 0-entry
  result (wrong line endings, wrong encoding) rolls back with the cause: "lines must end in CRLF".
- Placing the visible tag stays manual. The response ends with a hint: "Place it with
  Annotate → Keynote → Element (one click per element)". An alternative is an office tag
  family that labels the *Keynote* type parameter, placed with the existing `tag --type`; it
  shows the key, not the text. Not recommended as a default.
- **Not built:** `PostCommand(PostableCommand.KeynoteElement)` with the element preselected —
  it still waits for a user pick, and was not probed.

### 2.4 Files, risks, tests, size

- `KeynotesCommand.cs`, `KeynoteCommand.cs` (new). `Services/Claude/KeynoteArgs.cs` and a
  Revit-free `KeynoteFile` validator (line endings, tabs, parent keys) (new). `StatusVerbs`
  both maps; `CLAUDE.md`; README §6 becomes "🟡 keynote values and the table; ❌ keynote tags
  (API limitation)".
- Risks: a type keynote changes every instance (say so in the response). A load replaces the
  table for the whole model, so it must be undoable and announced. The cached table can be
  stale.
- Tests: `KeynoteFileTests` (CRLF/LF, UTF‑16/UTF‑8, missing parent, duplicate keys) and
  `KeynoteArgsTests`. Live: PRG1002 copy — load the probe file, `keynote 1561017 03300`,
  `keynotes 03`.
- Size **S**: 4 new files, ~300–400 lines including tests. Worth building only if the office
  starts keynoting. Today the template carries no keys, so it is the lowest priority of the three.

---

## 3. Dimensions in 3D views

### 3.1 Measured (probes `p5*_*.cs`)

Fresh project, two parallel 200 mm walls 3000 mm apart, an isometric 3D view:

| Case | Result |
|---|---|
| between the walls, **unlocked** view | ✅ 2800 mm |
| after `SaveOrientationAndLock`: between walls / wall thickness / wall length | ✅ 2800 / 200 / 5000 mm |
| wall height (vertical) with the default (horizontal) work plane | ❌ "The direction of dimension is invalid." |
| same after `View.SketchPlane` = vertical plane containing the line | ✅ 3000 mm |
| level → wall top, vertical plane | ✅ 3000 mm |
| line below a horizontal work plane | ✅ (the line need not lie in the plane) |
| perspective 3D view | ✅ (created, has a bounding box) |
| `IndependentTag` in the locked view | ✅ |
| 3D view placed on a sheet at 1:50 | ✅ all dimensions draw on the sheet (`img/dim3d - Sheet …`) |
| `SetOrientation` on the locked view | ❌ "View is locked and cannot be reoriented." |
| unlock + re-aim | ✅ all 10 dimensions survive, but the text is foreshortened in the new angle |

On PRG1002 (real model, duplicated `{3D}` + section box, locked): wall thickness via the
wall's two side faces ✅ 650 mm; pile spacing via the family's `CenterLeftRight` planes ✅
778 mm. The pile families expose **no Top/Bottom references**. A pile-length dimension from
instance-geometry faces was reported created (8960 mm) inside the transaction but **was gone
after commit**. In the `{3D}` copy (view direction straight down) a vertical dimension is
impossible (zero-length work-plane normal). Our `look` refuses a locked view with "has a locked
orientation (it is tagged)".

### 3.2 Rule Revit applies (inferred from the table)

The references must be parallel planes whose normal is the dimension direction *d* — the same
pairs `DimensionCommand.References` already finds. The view's **work plane must contain *d***.
A plane through the line with normal *n* = (view direction − *d*·(view direction·*d*)),
normalised, keeps the dimension facing the viewer as much as possible. It is undefined when
*d* is parallel to the view direction — refuse that case.

### 3.3 Verdict

✅ Feasible and small. The current command already finds the right references. What's missing:
accept a 3D view, choose *d* in model space, set the work plane per dimension, and check after
commit.

### 3.4 Command design

```
dimension <id> <id> --view <3d view|id> [--dir x|y|z|along|across] [--offset mm] [--no-lock]
dimension --levels --view <3d> --at <id>     # level string next to an element (later)
```

- `--view` works on **any** view without activating it (`NewDimension` takes the view). As a
  side benefit this removes the "activate in a separate call" dance for plans and sections too.
- In 3D, `x|y|z` are **project axes** (not the view's right/up). `along|across` follow a
  wall/beam's location line. The default follows the plan rules (one wall = its length;
  several = the distance between them; a single vertical element = its height).
- The view is locked (`SaveOrientationAndLock`) unless `--no-lock`, so the dimensions read as
  placed. The response says so and how to re-aim: "`look` needs the view unlocked; dimensions
  stay but foreshorten".
- After commit every new dimension id is re-read. A dimension Revit dropped is reported as
  refused (like `join` reports an accepted-but-not-applied join), with the hint "use centre
  planes / outer faces of the element instead of family geometry".
- Perspective views: refused by choice. Revit created the dimension (measured), but its look
  in a perspective was not checked, and a perspective has no paper scale to size the text by.

### 3.5 Files, risks, tests, size

- `DimensionCommand.cs`: a 3D branch (frame = model axes, work-plane setup, lock,
  post-commit check). `DimensionArgs` (in `SheetArgs.cs`): `--view`, `--dir z|along|across`,
  `--no-lock`. `SheetHelper.IsDimensionable`: allow non-template, non-perspective `ThreeD`.
  `LookCommand`: name dimensions in the locked-view message. `CLAUDE.md`; README §6.
- Risks: setting `View.SketchPlane` changes the view's work plane for the user — create it
  per dimension and restore the previous one. Instance-geometry face references can vanish at
  commit (measured), hence the re-read. Locking blocks `look` on that view (documented above).
- Tests: `DimensionMath` gets the work-plane normal function (*d* parallel to the view → null)
  and the 3D direction resolution. `SheetAssemblyTests` get the new args. Live: fresh project
  (walls, levels) and PRG1002 copy (wall thickness, pile spacing), then a sheet with the 3D
  view and `screenshot --view <sheet>`.
- Size **M** (smaller than legends): one command extended, ~300–450 lines including tests.

---

## 4. Recommended order

1. **`dimension --view`** (✅, M) — the largest gain for the least risk, and `--view` also helps
   2D sheets.
2. **`legend`** (✅, S–M) — duplicate a template legend + swatch/symbol rows only (decision above);
   no seed file, no legend components. Low priority.
3. **`keynotes`/`keynote`** (S) — only when the office starts keynoting. README §6 already
   changes now to the measured truth: keynote tags ❌ (API), keynote values possible.

Small fixes that need no plan: `viewport`'s same-sheet message for legends (§1.2), and the
README §5 wording: "the API can't create a legend from scratch; it can copy and fill one".
