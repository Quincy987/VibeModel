# VibeModel - AI Integration for Revit

## What This Is

VibeModel is a Revit add-in that embeds an HTTP server inside Revit, allowing AI assistants (Claude Code, etc.) to inspect and modify Revit models via simple curl commands. "Vibe modelling" — talk to your model in natural language.

## Build & Deploy

**Always use the build script** to close Revit, build, and restart automatically:
```powershell
.\build.ps1                      # Default: Revit 2023
.\build.ps1 -RevitVersion 2024   # Target specific version
.\build.ps1 -Debug               # Enable debug logging
```

Or via command prompt:
```cmd
build.cmd
build.cmd 2024
build.cmd --debug
```

**IMPORTANT**: Never ask the user to manually close/restart Revit. Always run the build script.

## Debug Logging
- Logs are in: `%LOCALAPPDATA%\VibeModel\logs\`
- Enable with environment variable: `VIBEMODEL_DEBUG=1`
- The `-Debug` flag on build.ps1 enables this automatically

---

## Talking to Revit (HTTP API)

VibeModel runs an HTTP server on `http://localhost:18884` inside Revit. Commands are instant (no polling delay).

**Port discovery (multiple Revit instances):** if 18884 is busy (e.g. a second Revit instance),
the server falls back to 18885–18888. Each live server writes
`%LOCALAPPDATA%\VibeModel\servers\<port>.json` (`{port, pid, startedUtc}`) on startup and deletes
it on shutdown — read those files (a file is stale if its `pid` is no longer running) or probe
`/health` on 18884–18888 to find the right port instead of assuming 18884.
Clients running **on this machine** (in-process code, or the CLI the chat pane spawns) should
address the server as `127.0.0.1`, not `localhost`: the listener binds IPv4 loopback only,
while `localhost` resolves to `::1` first — if that IPv6 attempt is silently dropped rather
than refused, the request stalls for its full timeout and looks like a dead server.

### Quick Start
```bash
curl -s http://localhost:18884/health          # Check connection
curl -s http://localhost:18884/help            # List all commands
curl -s http://localhost:18884/info            # Document info
curl -s http://localhost:18884/selected        # Inspect selected elements
```

### Output Format (text default, JSON opt-in)
Commands return **plain text by default** (unchanged). Request JSON with `?format=json` or an
explicit `Accept: application/json` header — curl's default `Accept: */*` stays text.
```bash
curl -s "http://localhost:18884/info?format=json"
curl -s -H "Accept: application/json" http://localhost:18884/info
```
JSON envelope: `{"ok": true, "data": {...}}` on success (or `{"ok": true, "text": "..."}` for
commands not yet migrated to structured data), and on failure
`{"ok": false, "error": {"code": "...", "message": "...", "suggestion": "..."}}`. In text mode,
errors are unchanged except for an additive `Hint: <suggestion>` line. `POST /batch?format=json`
returns `{"ok": <all-ok>, "atomic": bool, "rolledBack": bool, "results": [{command, args, result}]}`.
The Anthropic and Local LLM chat backends request JSON automatically (except `screenshot`).

### Model-State Stamp (change awareness)
Every command response carries a compact model-state stamp so you can tell when the USER
changed the model between your commands. Text mode appends one trailing line:
```
-- model #47 | view: {3D} Main | selected: 0
-- model #48 (2 USER edits since last command - model changed outside VibeModel) | view: Level 1 | selected: 1
```
JSON mode adds an additive `meta` sibling to the envelope:
`{"ok":true,"data":{...},"meta":{"edits":48,"userEditsSinceLastCommand":2,"activeView":"Level 1","selectedCount":1}}`.
`edits` counts every committed transaction in the document; `userEditsSinceLastCommand` counts
only edits NOT made through VibeModel (manual edits, undo/redo) since your previous command, and
resets once reported. **If the stamp reports user edits since your last command, the model changed
outside your control — re-read context (`/context`, `/selected`, `/screenshot`) before relying on
earlier query results.** Exceptions: `/health` and `/screenshot` are unstamped; a batch gets one
stamp for the whole response; text-mode error responses are unstamped (JSON errors still carry
`meta`); no useful stamp exists before a document is open.

### Revit failures (no modal dialog during a command)
While a VibeModel command runs, Revit's failure dialog ("Error - cannot be ignored", "Highlighted
walls overlap") never appears — it would block Revit's main thread (invisibly when Revit is
minimized) and time out every following request. `RevitFailurePolicy` decides, `RevitFailureHandler`
(`Services/Helpers/TransactionHelper.cs`) applies it — as the preprocessor of VibeModel's own
transactions and, through `Application.FailuresProcessing`, for transactions an `exec` snippet opens
itself. The user's own work in Revit is never touched.
- **Warning** → dismissed, the change is kept: `-- revit warning: Highlighted walls overlap. … (2×)`.
- **Error whose only failing elements are view-specific spot dimensions, dimensions or tags** with
  Revit's "Delete Element(s)" resolution → resolved that way, the change is kept:
  `-- revit resolved: Spot Dimension does not lie on its reference → deleted 1 annotation: 1730948 (Spot Elevations)`.
- **Any other error** → the transaction is rolled back and the command fails with Revit's own reason:
  `ERROR: Revit rolled the change back: <reason>` + a `Hint:` line (JSON code `REVIT_FAILURE`; `join`
  keeps its own report, with that reason in it). A commit Revit rolled back is never reported as success;
  when Revit rolls back without handing over a failure (live: a wall set to 1 mm high) the reason reads
  `Revit gave no reason (it could not rebuild the model with this change)`.
  When one failure in a pass forces a rollback, nothing else of that pass is applied or reported as
  kept. `exec` fails as soon as any of its transactions was rolled back (the hint says other
  transactions of the snippet were kept).

Lines go after a successful text response (before the stamp); JSON adds an additive `revit` object
`{warnings:[{message,count}], resolved:[{message,deleted:[{id,category}]}], rolledBack}` on the
envelope and per batch command. A batch reports per command and still undoes as one step; an atomic
batch rolls back when a command was refused — and then its per-command `-- revit` lines (e.g. deleted
annotations) were undone with everything else. From code: `FailureCollector.Current` (a `FailureReport`
— `DeletedIds`, `Resolved[*].Deleted`, `RollbackReason`) while a command executes.

### Optional Auth Token (off by default)
By default the server is **fully open** on loopback — no token, nothing changes. To gate access
(e.g. so other localhost processes can't call `exec`/`delete`), set the `VIBEMODEL_TOKEN`
environment variable **before launching Revit**. When set, every request except `/health` must
carry a matching `X-VibeModel-Token` header:
```bash
export VIBEMODEL_TOKEN="your-secret"     # set before starting Revit
curl -s -H "X-VibeModel-Token: $VIBEMODEL_TOKEN" http://localhost:18884/info
```
Missing/wrong token → `401` (`{"ok":false,"error":{"code":"unauthorized",...}}` in JSON mode,
`ERROR: Missing or invalid token.` in text mode). `/health` stays unauthenticated for liveness
probes. The in-app chat backends read the same env var and add the header automatically, so
enabling the token doesn't break in-Revit chat.

### Query Commands
```bash
curl -s http://localhost:18884/info                          # Document info
curl -s http://localhost:18884/selected                      # Selected elements
curl -s http://localhost:18884/params                        # Parameters of selected element
curl -s "http://localhost:18884/params?args=height"          # Filter parameters
curl -s "http://localhost:18884/list?args=walls"             # List elements (walls/floors/roofs/columns/beams/families/views/sheets)
curl -s "http://localhost:18884/get?args=12345"              # Get element by ID
curl -s "http://localhost:18884/select?args=123,456"         # Select elements
curl -s http://localhost:18884/family                        # Family info for selected
curl -s http://localhost:18884/categories                    # All categories
curl -s "http://localhost:18884/subcats?args=Structural"     # Subcategories
curl -s http://localhost:18884/views                         # All views
curl -s http://localhost:18884/levels                        # All levels
curl -s http://localhost:18884/activeview                    # Active view info
curl -s http://localhost:18884/context                       # Combined info + active view + selection (one call)
curl -s http://localhost:18884/screenshot                    # Export active view to PNG (default 1536px)
curl -s "http://localhost:18884/screenshot?args=1024"        # Custom long-edge pixels
curl -s http://localhost:18884/bbox                          # Bounding box of selected
curl -s http://localhost:18884/geometry                      # Geometry of selected
curl -s http://localhost:18884/familytypes                   # List available family types
curl -s "http://localhost:18884/familytypes?args=door"       # Filter by category/name
curl -s "http://localhost:18884/whyhidden?args=348122"       # Why is this element not visible in the active view?
curl -s "http://localhost:18884/whyhidden?args=348122 51203" # ...against a specific view
```

**`whyhidden` — visibility diagnosis.** Instead of guessing why an element does not draw
("just make a new view"), this reads the actual view state and names the setting responsible.
Every gate is probed in order and reported on its own line, prefixed `OK` / `BLOCKED:` /
`INFO` / `SKIPPED:`, with a `Fix:` line naming the exact Revit setting where one applies:
category + subcategory visibility and the VG master switches (1), per-element Hide in View and
Temporary Hide/Isolate (2), matching view filters and their visibility/overrides (3), detail
level — including which detail levels the family *does* draw at (4), view range / far clip vs
the element's bounding box (5), associated level, view discipline and datum (grid/level) 2D
extents (6), workset visibility, phase filter and design option (7), crop region and 3D section
box (8), and graphic overrides that render it invisible — 100% transparency, invisible line
pattern (9). It also cross-checks against Revit's own view element set ("Ground truth"), so it
never reports "all clear" for an element Revit genuinely excludes, and adds an `EDITABILITY`
section for visible-but-unmodifiable causes (group membership, pinned, linked model, workset
owned by another user). The report ends with a one-line `Verdict:` naming the probable cause.

### View & Analysis Commands
```bash
curl -s "http://localhost:18884/filter?args=walls"                      # Count/list by category
curl -s "http://localhost:18884/filter?args=walls Height > 3000"         # Operators: = != > < >= <= contains
curl -s "http://localhost:18884/filter?args=walls --view --select"       # Only the active view; select the hits (--ids: ids only)
curl -s "http://localhost:18884/filter?args=walls --count Mark"          # Group by a parameter value
curl -s "http://localhost:18884/measure?args=12345 67890"                # Distance between two elements (also: 6 coordinates mm, or 'selected')
curl -s "http://localhost:18884/whereused?args=67890"                    # Instances of a type per level (or: <family> <type>, loadable families)
curl -s "http://localhost:18884/zoomto?args=12345"                       # Zoom the active view to element(s) / 'selected'
curl -s "http://localhost:18884/isolate?args=walls"                      # Temporary isolate a category or ids; 'unisolate' clears it
curl -s "http://localhost:18884/unisolate"
curl -s "http://localhost:18884/hide?args=12345"                         # Hide a category or ids in the active view; 'show' reverses
curl -s "http://localhost:18884/show?args=12345"
curl -s "http://localhost:18884/colorsplash?args=walls Type"             # Colour by parameter value; 'colorsplash reset [category]'
curl -s "http://localhost:18884/chatlog?args=10"                         # Recent messages of the chat pane
```

### Source Data Commands (imported DWG + point clouds)
Read actual vector/point data from imported sources instead of guessing from screenshots.
```bash
curl -s http://localhost:18884/imports                            # List imported/linked CAD files (DWG)
curl -s "http://localhost:18884/layers?args=123456"               # DWG layers with object counts
curl -s "http://localhost:18884/curves?args=123456 pand --closed" # Closed footprint loops (mm, paste-ready for /floor)
curl -s "http://localhost:18884/curves?args=123456 pand --bbox 0 0 50000 50000 --page 2"  # Window + paging
curl -s http://localhost:18884/pointclouds                        # Loaded point clouds (.rcp/.rcs)
curl -s "http://localhost:18884/pcprobe?args=77 1500 2000"        # Ground level + height histogram at (x,y) mm
curl -s "http://localhost:18884/pcline?args=77 0 0 10000 0 500"   # Ground profile along a line (clutter-filtered)
```
`pcprobe`/`pcline` estimate ground as the lowest DENSE band of points, so parked cars,
trees and bins sitting on the surface are ignored; `pcline` additionally rejects
per-station spikes and interpolates occluded stations (flagged `interp`).

### Modification Commands
```bash
# Create a wall (coordinates in mm)
curl -s "http://localhost:18884/wall?args=0 0 5000 0 3000"

# Create a floor from points (mm)
curl -s "http://localhost:18884/floor?args=0,0 5000,0 5000,5000 0,5000"

# Create a grid line (mm) / level (mm) / view / room / sheet / tag
curl -s "http://localhost:18884/grid?args=0 0 10000 0 A"
curl -s "http://localhost:18884/level?args=3000 Level 2"
curl -s "http://localhost:18884/view?args=plan Level 2"
curl -s "http://localhost:18884/view?args=3d"
curl -s "http://localhost:18884/room?args=2500 2500 Office 101"
curl -s "http://localhost:18884/sheet?args=A1 Title Block 12345"
curl -s "http://localhost:18884/tag?args=12345"

# Delete elements
curl -s "http://localhost:18884/delete?args=12345 12346"

# Wall / floor type, level and offsets (type: exact name, else unique substring, else id)
curl -s "http://localhost:18884/wall?args=0 0 5000 0 3000 --type Generic - 300mm --level Level 1 --base-offset -400"
curl -s "http://localhost:18884/wall?args=0 0 5000 0 --type Timber --level Level 0 --top-level Level 2 --top-offset -100"
curl -s "http://localhost:18884/floor?args=0,0 5000,0 5000,5000 0,5000 --type Concrete 200 --level Level 1"

# Set parameter value (lengths/areas/volumes/angles in the unit `params` prints: mm, m², m³, °; other kinds internal;
# names with spaces: quote them, or Param=value)
curl -s "http://localhost:18884/set?args=12345 Comments Hello+World"
curl -s -G "http://localhost:18884/set" --data-urlencode 'args=12345 "Base Offset" -500'
curl -s -G "http://localhost:18884/set" --data-urlencode 'args=12345 Base Offset=-500'

# Color override (RGB 0-255)
curl -s "http://localhost:18884/color?args=12345 255 0 0"
curl -s "http://localhost:18884/color?args=12345 reset"

# Place family instance
curl -s "http://localhost:18884/place?args=Door Single-Flush 5000 2500"
curl -s "http://localhost:18884/placeid?args=67890 5000 2500"
curl -s -G "http://localhost:18884/placeid" --data-urlencode 'args=67890 5000 2500 -3000 --rotate 30 --set "Comments=pile A" --level Level 1'
curl -s -G "http://localhost:18884/place" --data-urlencode 'args=Pile "Concrete 320x320" --along 4711 --spacing 2800 --rotate auto --set "Height Offset From Level=-2500"'
curl -s "http://localhost:18884/placeid?args=67890 --along 0 0 12000 0 --count 6 --offset 500 --rotate auto+90"

# Aim a 3D view: camera at a compass direction from the target, shaded with edges, cropped to the target
curl -s "http://localhost:18884/view?args=3d --name Hoek --look zw --at 1543814,1543815"   # new 3D view, from the south-west
curl -s "http://localhost:18884/look?args=Hoek zo --at 1543814,1543815 --pitch 20 --box"     # re-aim an existing 3D view (+ section box)
curl -s "http://localhost:18884/look?args=iso"                                               # the active 3D view, at the whole model

# Join / cut geometry between two elements (with diagnostics on refusal)
curl -s "http://localhost:18884/join?args=12345 67890"            # JoinGeometryUtils.JoinGeometry
curl -s "http://localhost:18884/join?args=12345 67890 --switch"   # Flip which element wins the join
curl -s "http://localhost:18884/join?args=12345 67890 --cut"      # Solid-solid cut: A cuts B
curl -s "http://localhost:18884/unjoin?args=12345 67890"          # Remove join and/or cut

# Execute arbitrary C# code
curl -s "http://localhost:18884/exec?args=sb.AppendLine(doc.Title);"
```

**Modelling flags.** `wall`/`floor --type` take a type name (exact, else a unique case-insensitive
substring) or id; an ambiguous or unknown name lists the candidates; the response names the type.
`wall --top-level` replaces the height (a top offset needs it). `place`/`placeid`: `--rotate <deg>`
turns about the insertion point, counter-clockwise from project +X; `--set "Param=value"` (repeatable)
writes INSTANCE parameters — a plain number in the unit `params` prints (mm, m², m³, °), as in `set`; a
missing name is refused with the available names and nothing is placed. `z` is an elevation in the
same frame as x/y: a level-hosted family gets there through the offset parameter that actually moves
it (the visible one first), and the response names it. `--along <line id | x1 y1 x2 y2>` places along
a detail/model line, a wall/beam's location curve or two points, all in ONE transaction (one Ctrl+Z):
`--spacing s` alone fills the line end to end and reduces the spacing to divide it evenly (never more
than asked, 1 % slack), `--count n` alone spreads n evenly, both together are exact from the start;
`--offset` keeps that distance from the ends; `--rotate auto` (or `auto+90`) turns each instance to
the line direction; arcs are followed by parameter. The response lists the ids and the actual spacing.
`set` tries, in order: a quoted name, `Name=value`, then the longest leading words that name a
parameter — so the old 3-token form still works.
**`look`** (and `view 3d --look`): `n|no|o|zo|z|zw|w|nw` is where the camera STANDS seen from the
target (`zw` = south-west of it, looking north-east; English `ne/e/se/s/sw` too), 30° down
(`--pitch`); `top` = straight down, north up; `iso` = Revit's default; or `x y z` = the camera
position in mm. The target is `--at <ids | x y z | selected>`, default the model elements in the view.
The view is shaded with edges and its crop region fitted to the target (+`--margin`, default 2000 mm)
as seen from the new camera — refit on every `look`, since a crop only fits one camera; a point target
turns the crop off. `--box` adds a section box. Then `screenshot --view <id>`.

`join` always reports both categories, whether the bounding boxes actually intersect, which API
it called, and — on refusal — Revit's own reason in plain language plus the workaround that does
work (extend the solids so they really overlap; use a void family or Parts when a solid-solid cut
is refused; `--switch` when the wrong element wins). Use `--cut` for penetrations (a pile through
a quay wall, an H-beam inside a concrete pile); plain `join` for shared faces (a wing wall against
a capstone). `unjoin` removes whichever relation exists — join, cut, or both.

### Composite Profiles (profile / extrude / sweep / lwall)
Quay walls, capstones, aprons and curved runs as **real, schedulable families with a type** — not
`exec` DirectShapes (no Volume, no join). Each command generates a family on the fly
(`Application.NewFamilyDocument`), saves it, loads it and places it in ONE transaction group: one
Ctrl+Z, also inside `batch` (atomic included). Plan: `docs/plans/12-composite-profiles.md`.
```bash
curl -G -s "http://localhost:18884/profile" --data-urlencode "args=Deksteen --points 0,0 500,0 500,250 470,280 0,280"   # Profile family + VM_Profile
curl -s "http://localhost:18884/profile?args=Deksteen"                                        # Points, area, box, centroid; 'profile' lists them
curl -s "http://localhost:18884/extrude?args=Deksteen --along 4711 --material Beton"          # Line-based Generic Model along a line/wall id or x1 y1 x2 y2
curl -s "http://localhost:18884/extrude?args=Deksteen --along 0 0 10000 0 --category foundation"   # Point-based foundation with Wall length (or framing)
curl -s "http://localhost:18884/extrude?args=Damwandpaar --vertical 15000 --at 1000 2000 --rotate 30"  # Plan profile standing up (Height = type parameter)
curl -s "http://localhost:18884/sweep?args=Kade L --along 4711,4712,4713"                       # Along a chain of lines/arcs (any order) or a wall
curl -s "http://localhost:18884/lwall?args=-92722 14921 -73243 10385 --stem 600x4000 --foot 2500x800"  # L-wall: one parametric family, a type per size
curl -s "http://localhost:18884/lwall?args=--along 1543814 --stem 600x4000 --foot 2500x800 --toe 500 --side right --type-name KW1 --material Beton --phase new"
```
- **Profile convention** (all commands): a closed polygon in mm, **u = horizontal, positive to the
  LEFT of the run direction** x1,y1 → x2,y2 (the side `section` looks to), **v = up** from the line's
  elevation (the level + `--z`); (0,0) lies on the line. A repeated closing point, duplicates and
  collinear points are dropped, clockwise input is turned round; < 3 points, no area or a crossing
  outline (the segments are named) is refused. Lines only (arcs in a profile: phase 2). `--flip`
  mirrors u. `--vertical`: u,v = plan x,y around `--at`.
- **`lwall`**: the line is the stem's **front face**; the heel lies on `--side` (default left). Stem
  `<thickness>x<height>` (height = underside of the foot → top), foot `<width>x<thickness>` (width =
  the whole foot), `--toe` = how far the foot sticks out in front. Family `<prefix>L-wand` (line-based
  Generic Model; `--category foundation` → `<prefix>L-wand SF`) with type parameters Stem width,
  Height, Foot width, Foot thickness, Toe (Heel = Foot width − Toe) on locked reference planes — the
  user can flex it in Type Properties. Each size combination is a **type** named from the sizes
  (`600x4000 voet 2500x800`, pack `profiles.typeName`), an existing type is reused (refused when its
  sizes were changed in Revit); never a second family. `--slope <mm>` (foot thickness at the back)
  is not parametric yet: a fixed-profile family per size, and the response says so.
- **Families**: `<prefix><profile>` (Generic Models, line-based), `… SF` (foundation), `… beam`
  (framing — a real beam: analytical member, the section hangs from its top at the line),
  `… vertical`, `… sweep <first id>` (or `--name`), the profile itself `<prefix><name> profile`
  (Profiles: Revit's Wall Sweep / Slab Edge tools use it too). Prefix from the pack
  (`profiles.familyPrefix`, default `VM_`). Parameters: `Material` (type), `VM_Length` (shared,
  instance, `= Length` / `Wall length`; the sweep's path length) and the hidden `VM_Source` stamp,
  from a shared-parameter file with fixed GUIDs (`%LOCALAPPDATA%\VibeModel\VibeModel.SharedParameters.txt`,
  or the pack's office file + names). Revit's `SharedParametersFilename` is swapped for the build and
  always restored.
- **Re-running updates, never "(2)"**: same profile/path → the loaded family is reused (no
  generation); changed points or path → regenerated and reloaded with `overwriteParameterValues =
  false` (project types and their values stay, existing instances follow; a sweep's instance moves to
  the new path start). A family changed by hand since (Edit Family → Load into Project — detected by
  the structure hash in `VM_Source` when the family would be overwritten) or one VibeModel did not
  generate is refused until `--replace`. A reused family is not opened (no `EditFamily` per `lwall`),
  so a hand edit shows up as `WARN: volume … but the profile gives …` (Revit's volume vs profile area ×
  length, straight runs, not joined); `--replace` always regenerates, also with unchanged points.
- **Files**: built in `%LOCALAPPDATA%\VibeModel\families\_staging\<pid>\`, loaded from there, then
  copied to `<project folder>\VibeModel\families\<family>.rfa` (pack `profiles.folder`, relative to the
  project folder) with the 3 s project-folder deadline; offline → `…\families\_offline\`, said in the
  response. The model never depends on the file. Templates: pack `profiles.familyTemplates.<kind>`
  (`lineBased|generic|foundation|framing|profile`; a network template is cached with a deadline),
  else the Autodesk metric templates (Revit's template folder, then
  `C:\ProgramData\Autodesk\RVT <ver>\Family Templates\English\`).
- **Placement**: phase via `PhaseDefaults` like `place` (`--phase`), `--set "P=v"` (instance),
  `--material` (type parameter: every instance of that type follows — the response says so), the
  post-command check and the journal (`lwall … · created … · wrote VM_L-wand.rfa`). The new element is
  joined with every wall, floor and generated element it really overlaps (never library families such
  as piles); `--no-join` skips that. Response: family (new / reused / updated), type, profile area,
  side (compass), instance id, length, **Volume** (from Revit), underside/top, material, phase, joins,
  file, and a `section` / `schedule … VM_Length,Volume` next step.

### Batch Commands (POST)
A batch runs as **one undo unit** — the whole batch collapses to a single Ctrl+Z in Revit.
Send a multi-step "vibe" as one `/batch` to get one-undo behavior (rather than many single calls).
```bash
curl -s -X POST http://localhost:18884/batch -d "wall 0 0 5000 0
wall 5000 0 5000 5000
wall 5000 5000 0 5000
wall 0 5000 0 0"
```

**Failure handling (default: resilient).** If a command fails mid-batch, the others still
succeed and are kept as one undo unit; the response shows the per-command `ERROR`. Each command
self-rolls-back, so a failure never leaves half-built geometry.

**Atomic mode (opt-in, all-or-nothing).** Any failure rolls back the entire batch. Trigger with a
leading `#atomic` line or `?atomic=1`:
```bash
curl -s -X POST "http://localhost:18884/batch?atomic=1" -d "wall 0 0 5000 0
wall 5000 0 5000 5000"
```
A read-only batch (no modifications) creates no undo entry.

### Sheet Assembly (section / crop / viewport / dimension / spot / text / sheetinfo / schedule / callout / region / export)
Build a drawing sheet from the conversation: sections, views on a sheet, dimensions, notes —
and read the result back without looking at the Revit UI.
```bash
curl -s "http://localhost:18884/section?args=-92722 14921 -73243 10385 --name Doorsnede 1-1 --scale 50"  # Along a line (mm)
curl -s "http://localhost:18884/section?args=1543814 --offset 2000 --depth 1000"     # Straight across a wall, at its midpoint
curl -s "http://localhost:18884/crop?args=Bovenaanzicht --fit --margin 2000"                       # Crop to the model elements (DWG/point clouds ignored)
curl -s "http://localhost:18884/crop?args=Bovenaanzicht --box 100000 -10000 230000 10000"            # ...or to a rectangle; 3D views get a section box
curl -s "http://localhost:18884/sheet?args=--number 0001 --name Situatie 0 --set \"Drawn By=J. Jansen\""
curl -s "http://localhost:18884/viewport?args=0001 101,102,103,104 --grid 2x2 --scale 50"            # Grid over the free sheet area
curl -s "http://localhost:18884/viewport?args=0001 Bovenaanzicht --at 500 650"                       # One view centred at paper mm
curl -s "http://localhost:18884/viewport?args=0001 201,202,203 --grid 3x1 --area 20 20 980 300"      # Grid inside a band
curl -s "http://localhost:18884/viewport?args=--move 4711 300 200"                                   # Nudge (paper mm); --remove <id>
curl -s "http://localhost:18884/sheetinfo?args=0001"                                                 # Paper, stamp, viewports, overlap/off-paper checks
curl -s "http://localhost:18884/screenshot?args=--view 0001"                                         # PNG of a sheet/view without activating it
curl -s "http://localhost:18884/dimension?args=1543814 1543815"                                      # Through elements (active view)
curl -s "http://localhost:18884/dimension?args=1543814 --dir y"                                      # x|y = the view's horizontal/vertical
curl -s "http://localhost:18884/dimension?args=--points 0 0 5000 0"                                  # Nearest face/grid to each point (plan)
curl -s "http://localhost:18884/dimension?args=--levels"                                             # Level string in the active section
curl -s "http://localhost:18884/text?args=-1200 850 Damwand AZ26-700\nlg. 15.05m --size 2.5 --leader -400 -1030"
curl -s "http://localhost:18884/tag?args=1543814 --leader --at 1000 2000"
curl -s "http://localhost:18884/spot?args=103000 2000"                                               # Spot elevation (peilmaat) at a point, active view
curl -s "http://localhost:18884/spot?args=1730980 bottom"                                            # ...on an element's top|bottom (a pile's tip)
curl -s "http://localhost:18884/spot?args=101000 2000 --fixed --leader 104000 3500"                  # ...on a short detail line: never moves
curl -s "http://localhost:18884/whatdepends?args=1730959 1730980"                                    # Read-only: annotations/hosted/joined on elements
curl -s "http://localhost:18884/list?args=dimensions --view"                                         # Values in mm; also 'list text', 'list sheets' (view counts)
curl -G -s "http://localhost:18884/schedule" --data-urlencode "args=structural foundations --fields \"Family and Type,Count,Length\" --group Family and Type --total --name Palenstaat"
curl -G -s "http://localhost:18884/schedule" --data-urlencode "args=walls --fields \"Type,Length\" --filter \"Length gt 12000\""   # Filter (display units)
curl -s "http://localhost:18884/schedule?args=Palenstaat --rows"                                     # Read any schedule back (+ the sheets it is on)
curl -s "http://localhost:18884/callout?args=Bovenaanzicht --box 100000 -5000 110000 5000 --name Detail A --scale 20"  # Plan callout ( --section: detail view)
curl -s "http://localhost:18884/callout?args=101 --box 0 0 4000 3000 --reference Detail A"            # Reference callout to an existing view
curl -s "http://localhost:18884/region?args=Bovenaanzicht --points 0,0 4000,0 4000,3000 0,3000 --type Beton"  # Filled region (view frame)
curl -G -s "http://localhost:18884/region" --data-urlencode "args=Doorsnede 1-1 --detail \"Grondslag : 600\" --at -1200 850 --rotate 90"  # Detail component
curl -s "http://localhost:18884/list?args=regions --view"                                            # Filled regions + detail items (type, m², view)
curl -s "http://localhost:18884/viewport?args=--move 4711 --to 0002 --at 400 300"                    # View to another sheet (keeps scale/title)
curl -s "http://localhost:18884/export?args=pdf 0001,0002"                                           # → <project folder>\export\0001 - <name>.pdf
curl -s "http://localhost:18884/export?args=dwg --all-sheets --to C:\out"                            # Office DWG setup when the pack names one
curl -s "http://localhost:18884/export?args=ifc"                                                     # Whole model, default IFC setup (or one view id)
```
- **Coordinates.** Paper mm are measured from the sheet's lower-left (A0 = 1189×841). `text`, `tag --at`,
  `dimension --levels <x>` and `crop --box` use the view's own frame: plan → project x,y mm;
  section/elevation → x = position along the view (project x,y projected on its horizontal axis — the
  `section` response prints the range; it does not move when the view is cropped), y = elevation mm;
  sheet → paper mm.
- **`section`** looks to the LEFT of the line direction x1,y1→x2,y2 (the response names the compass
  direction). Default depth 3000 mm; the vertical range comes from the model elements it cuts ±1 m
  (`--top/--bottom` override). An active office pack's `viewDefaults.section` template/phase applies.
- **`crop`** first: an uncropped view spans everything visible — with a BGT underlay hundreds of
  metres — so it never fits a cell. `--fit` crops to the largest cluster of model elements (DWG,
  point clouds, links, topography ignored; far-off stragglers dropped and counted) or to `--elements`,
  `--box` to a rectangle, `--off` undoes; the response gives the paper size at the view's scale. On an
  already-cropped view `--fit` can only shrink (`--off` first).
- **`viewport`** keeps the margin (default 10 mm) and, when the title block is a full-sheet border, a
  200 mm strip on the right for the stamp (`--reserve 0` / `--area` to change). A view that does not fit
  its cell is still placed and gets a `WARN` naming the scale that would fit. A view already on another
  sheet is refused with that sheet's number. Schedules are placed too. `--title off` uses (or creates) a
  viewport type without a title.
- **`sheetinfo`** grades box overlaps: a thin strip (≤ 30 mm) is `INFO` (usually empty crop margin),
  anything bigger `WARN`; office stamp patterns (`NL_META_*`, `X_1..5`) expand to the real fields.
- **`dimension`** needs a plan/section/elevation as the ACTIVE view (`activate` it in a separate call —
  a view switch lands after the request). Per element it uses the outer two faces perpendicular to the
  dimension direction; grids, levels, reference planes and family centre planes are used directly.
  A face that lies on a level (vertical dimension) or a grid / reference plane (horizontal) within 1 mm,
  and that the view shows, is dimensioned to that datum instead ("level 'B.K. …' (at the Floors face
  of 1730959)") — same value, survives the element being replaced. The response lists `Attached to:`
  per reference (`stable (level)` / `element face of <id> (Floors, created by VibeModel this session)`)
  and a `Note:` when a reference is a floor/foundation/wall face or a VibeModel-created element; JSON
  `references[]` gains `stable` and `vibeModelCreated`. Should Revit refuse the datum version (an extent
  that does not reach), the faces themselves are dimensioned and a `Note:` says why. A family instance's face is referenced as the
  instance's own (`<instance>:0:INSTANCE:<symbol ref>`): instance geometry hands out references to
  the family symbol, and a dimension on those hangs on the symbol (outlives the pile, invisible to a
  scan for it). Revit's refusal message is returned verbatim with the usual cause.
- **`spot`** (peilmaat) needs the same ACTIVE plan/section/elevation/detail view. `spot <x> <y>` snaps
  (≤ 100 mm, in the view plane) to the nearest reference: a face the view CUTS (system families), else
  the edge the view shows beyond the cut, and always an edge for family instances — measured: Revit
  refuses a spot on an instance face in a section ("does not lie on its reference") and on a face behind
  the cut ("no valid coordinates in the view"). Ties prefer a cut face, then a level edge (a pile's tip
  square), then the element nearest the viewer. In a plan: the highest upward face at that x,y.
  `spot <id> top|bottom`: the highest/lowest horizontal face (system; its near edge when not cut) or the
  extreme level edge (instance), at the element's middle moved onto the cut plane. Default leader:
  shoulder 12 paper mm above (top) / below (bottom), text 12 mm to the right; `--leader x y` moves the
  text (view frame, the shoulder stays vertical over the point). `--fixed` hangs it on a 200 mm detail
  line through the point (sections only — in a plan a detail line has the level's height): it never
  moves or breaks, and never follows the model. `--type` = a spot elevation type (exact, else unique
  substring; default: the document's). The response gives the value, `Attached to: floor 1730959
  'Deksteen_200' — top face at x=103000 (follows the element)` and a `Warning:` when that element was
  created by VibeModel this session or is a floor/foundation/wall (likely to be replaced).
- **`whatdepends <ids|selected>`** (read-only): per element the spot elevations, dimensions and tags that
  reference it (with view and sheet), hosted family instances, joined elements and "created by VibeModel
  this session", then a `Verdict:` (how many annotations in which views a replacement affects). Revit's
  dependent-element list misses an instance's edge-referenced spot, so annotations are also scanned.
- **Annotations survive replacements.** Measured in Revit 2023: most host changes through the API
  raise no failure — deleting a floor silently deletes its spot and a 3-reference dimension; a wall+floor
  dimension survives with only the wall references (0 segments, reads wrong on the sheet); a pile's
  type/parameter change can leave its tip spot with a stale value; shape-editing a floor invalidates the
  spot on it. Around `delete`, `exec`, `set`, `join`, `unjoin`, `phase` (and any batch holding one) the
  `AnnotationGuard` records the spots, dimensions and tags on the elements the command names (a type id
  covers its instances; every annotation in the document for `exec`; skipped above 5000, logged once,
  and the capture time is logged above 200 ms). Afterwards it only looks at annotations whose referenced
  element the command deleted or changed — an annotation the command removed itself (named in `delete`,
  or deleted by an `exec` snippet while its host stayed untouched) stays removed. A spot whose element
  survived but moved or went stale is refreshed on the same element; a lost spot moves to a replacement
  with a face/edge at the same height (±1 mm) within 50 mm (a 350 pile replacing a 320 one keeps its tip
  spot); a dimension is re-created with every lost face replaced by a face on the old reference plane
  (±1 mm, parallel, seen edge-on — also a new face of the same, changed element); a tag moves to the
  replacement covering the old element's middle. A replacement is an element of the same category near
  the old one that the command added, or that VibeModel created earlier this session (placed in a
  previous call) — never an unrelated neighbour, so deleting one floor does not move its spot onto the
  next. What cannot be re-hosted is deleted — never an invalid annotation left behind — and listed: `-- annotations: re-hosted 2 spot elevations (1730946 → 1730963 on
  structural foundation 1730950, …); removed 1 dimension that referenced 1730914 (view Doorsnede 1-1,
  sheet 0003)`, after the `-- revit`/`-- check` lines (JSON: additive `annotations` object
  `{rehosted:[{from,to,kind,host,hosts,view}], removed:[{id,kind,referenced,view,sheet,by}]}`, `by` =
  `revit` when Revit's failure policy deleted it — marked `(Revit)` in text). The re-host is in the same
  undo step (a TransactionGroup) — except `exec`, whose snippet may save, open or close documents (not
  allowed inside a group), so there it is its own undo step. A rolled-back atomic batch is not
  reconciled. Nothing is touched when no recorded annotation changed. Pure parts (wording, tolerances,
  scope): `AnnotationReport.cs`; Revit side: `AnnotationGuard.cs`.
- **`schedule`** matches fields by the names Revit lists (exact, else a unique substring; unknown →
  the schedulable fields of that category). `--filter` numbers are in the project's display unit;
  a filter field outside `--fields` is added as a hidden column. `--group` = not itemized, one row per
  value with summable columns totalled; `--total` adds the grand total. Without `--group` rows are
  sorted by the first field. The text table is cut at 20 rows (`--rows [n]` reads up to 200/n).
  New schedules get their columns widened to the longest cell, so type names do not wrap on the sheet.
- **`callout`** from a plan uses the plan's own view type (a plan callout); `--section`, and any
  callout in a section/elevation, makes a detail view. The `--box` must lie inside the parent's crop.
- **`region`** uses `text`'s coordinate frame (plan: project mm; section: along/elevation; sheet:
  paper mm). Line-based detail items are refused (place them with `exec`).
- **`viewport --move <id> --to <sheet>`** keeps the paper position when it lies in the target's free
  area, else centres the view there; `--at x y` overrides. The old viewport id is gone afterwards.
- **`export`** never runs inside a transaction (an atomic batch is refused); names collide →
  " (2)". PDF uses the sheet's own paper size, crop/scope boxes/reference planes hidden. DWG
  without a setup merges views into the sheet (no xref files). IFC runs in a rolled-back
  transaction, so it never changes the model, and is read back: the response lists per category how
  many Revit elements went in and as which IFC class, and which model elements in scope are missing.
  Revit 2023's default class table maps Structural Foundations to `IfcSlab BASESLAB`, so piles arrive
  as slabs, not `IfcPile`/`IfcFooting` (the response says so). Default folder: `<project folder>\export\`; a `--to`
  folder that does not exist is created (relative paths are under the project folder).
- **Out of scope:** legend views cannot be created through the Revit API (duplicate an existing one via
  `exec`); annotating from a specification is chat work on top of `text` (plan 10/11 documents).

### Office Standards (template / standards / phase / check / coords / library / loadfamily / linkcad / linkrvt)
An office **standards pack** (folder with `pack.json`, `core.md`, topic sheets, `checks.json` —
see `standards/README.md`; sample: `standards/example-office/`) teaches VibeModel how an office
models. A pack is active for a document only when its **fingerprint** matches what the document
actually contains (its phases and a bound shared parameter; scope boxes/views only as evidence). Without a matching pack
every command behaves exactly as before.
```bash
curl -s http://localhost:18884/template                         # Live setup inventory + which pack matches (fingerprint)
curl -s http://localhost:18884/standards                        # Active pack, topics, where packs were searched
curl -s "http://localhost:18884/standards?args=phasing"         # One topic sheet (verbatim)
curl -s "http://localhost:18884/standards?args=core"            # The always-on rules block
curl -s "http://localhost:18884/standards?args=--refresh"       # Re-read packs after editing them
curl -s "http://localhost:18884/standards?args=--prompt"        # Exactly what the chat receives this turn
curl -s "http://localhost:18884/standards?args=--index"         # Index the office source documents (PDF/txt) in the background
curl -s "http://localhost:18884/standards?args=docs"            # Indexed documents + chapters
curl -s "http://localhost:18884/standards?args=leidraad 4.2"    # One chapter of a source document (+ its subchapters)
curl -s "http://localhost:18884/standards?args=distill"         # Optional: LLM-distil the manual into core.generated.md
curl -s "http://localhost:18884/wall?args=0 0 5000 0 3000 --phase temp"   # Creating commands take --phase
curl -s "http://localhost:18884/phase?args=12345 12346 demolish"          # Re-phase existing elements (office table)
curl -s http://localhost:18884/check                            # Audit vs the pack's checks.json (OK/WARN/FAIL + Fix + Verdict)
curl -s "http://localhost:18884/check?args=selected"            # Element-level rules on the selection only
curl -s "http://localhost:18884/check?args=--stats"             # Which rules fire most (feedback for the next pack version)
curl -s http://localhost:18884/coords                           # Survey point / base point / shared coordinates / pins
curl -s "http://localhost:18884/library?args=damwand"           # Search the office family library (cached index)
curl -s "http://localhost:18884/loadfamily?args=\"X:\lib\Pile.rfa\""      # Load a library hit
curl -s "http://localhost:18884/linkcad?args=\"X:\base\map.dwg\" --first" # Link (never import) with the office link settings, pinned
curl -s "http://localhost:18884/linkrvt?args=\"X:\proj\K-model.rvt\""     # Revit model / IFC by shared coordinates, pinned (refused without coordinates)
curl -s "http://localhost:18884/view?args=plan Level 2 --name Werkview --activate"  # Office work view, named, made the active view
curl -s "http://localhost:18884/activate?args=Werkview"               # Activate a view by name or id (also works after exec)
```
- **Default phase.** With an active pack, `wall`, `floor`, `place`, `placeid` and `room` set
  Phase Created/Demolished from the pack's `phaseTable` (default kind, usually `new`) and add a
  `phase: …` line — including a warning when the active view's phase cannot show the element.
  `--phase existing|new|temp|demolish|future` (or a pack alias) overrides.
- **Office views / sheets.** `view plan|3d` applies the pack's view template, phase, phase filter
  and browser-folder parameters (`--template <name>` / `--no-template` override, `--name <viewName>`;
  flag values run to the next `--` flag, so put flags after the level name; `--activate` opens the new
  view — new work on the office phase is invisible in the template's existing-phase work views); `sheet` without
  a title block uses the pack default. Every automatic choice is printed.
- **Orientation and links.** `check` also judges `project-north-true` (Project North never rotated
  from True North — `coords` warns too), `reference-point-true-north` (the reference point family
  unrotated and pinned) and `links-by-shared-coordinates` (every Revit link where *By Shared
  Coordinates* puts it; unloaded and CAD links are reported as not verifiable, never flagged).
  `linkcad`/`linkrvt` name the criterion they placed by ("coordinates set up: base point N/S …").
- **Post-command check.** After any modifying command or batch, the element-level rules run on
  exactly what it created; problems add one line: `-- check: 1 WARN (param-filled) → run 'check' for fixes`
  (JSON: an additive `check` object).
- **Prompt.** Each chat turn gets a `TEMPLATE:` block (≤1 kB, live facts incl. the active view's
  phase) and, when a pack is active, a `STANDARDS:` block (the pack's core rules, ≤`coreMaxBytes`).
- **Where packs come from** (first id wins): Settings → *Standards pack folder* (`StandardsPackPath`,
  e.g. the office share) → `%LOCALAPPDATA%\VibeModel\standards\<pack>\` → `<add-in folder>\standards\`.
  A network `StandardsPackPath` is mirrored to `%LOCALAPPDATA%\VibeModel\standards\_mirror\` in the
  background (startup, `--refresh`, Settings → Save) and only the mirror is read.
  Settings → *Office source documents folder* (`StandardsFolder`) is what `standards --index` reads.
  Settings → *Apply office standards* (`StandardsEnabled`) switches everything off.
- Network shares are never read on Revit's main thread without a deadline: the library and the
  source documents are indexed in the background and cached under
  `%LOCALAPPDATA%\VibeModel\standards\_cache\<pack>\`; PDF text comes from a separate helper
  process (`VibeModel.PdfText.exe`, deployed next to the add-in).
- Real office packs contain internal paths — keep them out of this public repo (`standards/*` is
  gitignored except the README and the example pack).

### Project Folder (docs / readdoc)
Each model has a **project folder** with its reference documents (bestek, memo's, DO-tekening
PDF, inspection reports). Default: the folder the open `.rvt` lives in. The chat pane's
Settings → *Project folder* (Browse… / Clear) overrides it for the current project only
(`ProjectContextFolders` in settings.json, keyed by the sanitized document title — the same key
as the attachment store). Unsaved / cloud models have no default. Every output names the folder
and its source: `(map van het model)` (default) or `(ingesteld)` (configured).
```bash
curl -s http://localhost:18884/docs                               # Manifest: names, kinds, sizes (one level deep, ≤200)
curl -s "http://localhost:18884/docs?args=survey"                 # One subfolder
curl -s "http://localhost:18884/readdoc?args=memo.txt"            # Text inline (400k-char cap)
curl -s "http://localhost:18884/readdoc?args=DEMO001-+DO.pdf"     # PDF text via the PDF helper, else path + hint
curl -s "http://localhost:18884/readdoc?args=bestek"              # Unique prefix ok; ambiguous lists the matches
```
- Read-only, and only inside the folder: `..`, absolute paths, prefix siblings (`docs-x` vs
  `docs`) and junctions/symlinks out are refused. Revit's own files (`.rvt`/`.rfa`, numbered
  backups, `*_backup` and `… Support` folders) are not listed.
- `readdoc`: text kinds inline; `.pdf` through `VibeModel.PdfText.exe` (cached under
  `%LOCALAPPDATA%\VibeModel\projectdocs\_cache\`; if extraction takes >12 s it keeps running and
  the next call returns it); `.docx` as paragraph text; images and other kinds as the absolute path + a hint.
- Every chat turn carries a bounded `PROJECT FOLDER` block (the same manifest, ≤100 entries);
  the Claude Code backend additionally runs with `--add-dir <folder>`.
- Listing a folder on a network drive has a 3 s deadline; an offline folder is reported, never waited on.

### Project memory & journal (remember / memory / forget / journal)
Per project, next to the reference documents: `VibeModel\memory.md` and `VibeModel\journal.md`
in the project folder (see above); a model without a folder uses
`%LOCALAPPDATA%\VibeModel\projects\<sanitized title>\`. Both are plain markdown, editable by hand.
```bash
curl -s -G http://localhost:18884/remember --data-urlencode "args=doorsnedes krijgen fase 2. Bouwfase"   # Dated fact; same fact (normalized) not saved twice
curl -s http://localhost:18884/memory                          # Numbered, oldest first
curl -s "http://localhost:18884/forget?args=2"                 # By number, exact text or a unique fragment
curl -s "http://localhost:18884/memory?args=--clear"           # Empty it (the header stays)
curl -s "http://localhost:18884/journal?args=today"            # Also: yesterday, 2026-10-12, --last 50 (default: last 20)
```
- **Memory in the prompt.** Every chat turn (all backends) carries a `PROJECT MEMORY` block next
  to PROJECT FOLDER: ≤ 2 kB, newest last, `(+N older — run memory …)` when cut, plus the rule to
  save project decisions/preferences/corrections with `remember` (one line, the user's words)
  and to read the block before answering "last time" questions (`ProjectMemory.PromptRule`).
- **Journal lines** (`ProjectJournal`): one per modifying command / batch that changed the model,
  written by `RevitCommandHandler` from the `DocumentChanged` ids
  (`- 2026-10-12 14:03:12 · wall 0 0 5000 0 3000 · created 1543814 · modified 1543700 · chat t3f9a2c "maak een muur"`),
  and one per chat turn with the `[bron: …]` / `[source: …]` markers its answer cited
  (`… · chat t3f9a2c "…" · sources: DEMO001- DO.pdf p.3`). A command that changed nothing (an
  `exec` that only read) is not journaled; `export` changes nothing but is (`IJournaledCommand`:
  `… · export pdf 0001 · wrote 0001 - Situatie.pdf · chat …`). The turn comes from the `X-VibeModel-Turn` header
  (`<id> <url-encoded question>`, sent by the API backends) or else from the chat turn in flight
  in this Revit session (the Claude Code CLI's own curls) — an external curl made during a chat
  turn is attributed to that turn too.
- **Never blocks Revit:** journal lines are queued; a background writer resolves the folder and
  appends with a 3 s deadline; an offline folder drops lines with one logged warning and is
  retried after 60 s. `journal` flushes the queue first. Rotation at 2 MB → `journal-<yyyymm>.md`
  (`-2`, `-3` … when taken). Memory commands read/write with the same 3 s deadline.
- `remember` / `memory` / `forget` / `journal` are not modifications (no transaction, no undo
  step). `docs` does not list the `VibeModel` subfolder.
- **Switch:** Settings → *Projectgeheugen en journaal bijhouden* (`ProjectMemoryEnabled`, default
  on). Off: no block, no header, nothing written, and the four commands (`IProjectMemoryCommand`)
  are left out of the prompt file, the tool lists and `help` — the exact prompt as before.

### Connecting to Claude (wizard, keys, managed settings, Diagnose)
Built for modellers without admin rights or a terminal; IT rollout is in `docs/deployment.md`.
- **Backend choice** (`VibeModelApp.CreateChatBackend`, `PreferredBackend` = `auto`): a configured API
  key wins → `AnthropicDirectBackend`; else Claude Code when found and not disabled; else a reachable
  local LLM; else the not-configured direct backend, whose welcome card is the **connect wizard**
  (`UI/ConnectWizardView.cs`): paste a key → `AnthropicHttp.TestKey` (`GET /v1/models/{model}`, free;
  200 ok / 401 wrong key / 403 no access / 404 unknown model / network) → stored only when accepted;
  or Claude Code → **Install** (`ClaudeSetup.InstallStartInfo`: visible PowerShell running
  `irm https://claude.ai/install.ps1 | iex`; `VIBEMODEL_DRY_RUN_INSTALL=1` logs it instead), **Log in**
  (opens `claude` in `%LOCALAPPDATA%\VibeModel`), **Use Claude Code**. Choosing raises
  `BackendChangeRequested`; no restart. AutomationIds: `WizardApiKeyBox`, `WizardApiTest`, `WizardApiSave`,
  `WizardApiStatus`, `WizardCliStatus`, `WizardCliInstall`, `WizardCliLogin`, `WizardCliUse`, `WizardRecheck`.
- **API key** (`SettingsManager.GetApiKey(out source)`): managed.json → `ANTHROPIC_API_KEY` → settings,
  where it is DPAPI-encrypted for the Windows user (`AnthropicApiKeyProtected`); a plain-text
  `AnthropicApiKey` from an older build is encrypted on first read and removed. Shown only masked
  (`MaskKey`: `sk-ant-…1234`). Settings never copies a managed/environment key into settings.json.
- **managed.json** (`Infrastructure/ManagedSettings.cs`, read-only, `%ProgramData%\VibeModel\` then
  `%APPDATA%\VibeModel\`, re-read at most once a minute): `PreferredBackend`, `Model`, `ApiBaseUrl`,
  `AnthropicApiKey`, `DisableClaudeCode`, `ClaudeCliPath`, `Language` override settings.json and are locked
  in Settings ("beheerd door IT").
- **HTTP**: every Anthropic call (chat, quick replies, distiller, key test) uses `AnthropicHttp`:
  `SettingsManager.MessagesUrl(GetApiBaseUrl())`, Windows proxy with the user's credentials for the
  proxy only, the Windows certificate store (validation never relaxed). TLS: Revit's process-wide
  `ServicePointManager.SecurityProtocol` is left at SystemDefault (TLS 1.2/1.3); only a pinned list
  without TLS 1.2 gets it added (`EnsureTls12`).
- **Claude Code discovery** (`ClaudeCliLocator`): explicit `ClaudeCliPath` (managed → `VIBEMODEL_CLAUDE_CLI`
  → settings; `none` = off — how a laptop without Claude Code is simulated) else
  `%USERPROFILE%\.local\bin\claude.exe`, the PATH as stored in the registry now (Revit's own PATH is
  from its start), the npm folders. `ClaudeCodeBackend.LastSearch` lists what was checked. The Windows
  proxy is handed to the CLI as `HTTPS_PROXY` (+ `NO_PROXY=127.0.0.1,localhost,::1`) when no proxy
  variable is set — never `HTTP_PROXY`: curl would route the CLI's calls to http://127.0.0.1 through it
  (`ClaudeCodeBackend.ProxyVariables`).
- **Thinking models on the direct backend** (Sonnet 5.5 default): thinking/redacted_thinking blocks are
  kept and sent back; the system prompt is built once per user turn (`_turnSystemPrompt`); all but the
  newest screenshot image and over-budget attachments are dropped only at a turn start, and then
  (or when the system prompt changed) earlier thinking blocks are removed (`StripThinkingBlocks`); a 400
  mentioning thinking/signature is retried once without them (only when there are any). `refusal` → `Strings.ApiRefusal`;
  `max_tokens` → a note.
- **Cost line**: `ChatActivityEvent.ForUsage(TurnUsage)` (API: `message_start`/`message_delta` usage;
  CLI: the result event's `usage` + `total_cost_usd`) → `TurnActivity.Usage` → one muted line under the
  answer (`ModelPricing.Describe`, AutomationId `TurnUsage`), never stored. Prices: `ModelPricing` table.
- **Diagnose** (header button → `UI/DiagnoseWindow.cs`; `diagnose` command): `Services/Diagnostics/
  DiagnoseReport.Build()` — version, add-in path, Authenticode (WinVerifyTrust, cache-only), Revit,
  port, backend + key source (masked), CLI search, managed.json, proxy, DNS + HTTPS to the API host and
  claude.ai with the certificate issuer, this session's warnings/errors (`Logger.Recent()`), settings
  with secrets masked. Probes run in parallel under one 6 s deadline (the command runs on Revit's
  main thread; measured 0.6 s). `Logger` now always writes WARN/ERROR; INFO only with `VIBEMODEL_DEBUG=1`.
- **Signing, measured in Revit 2023**: unsigned → "Security - Unsigned Add-In" once per new DLL
  (*Always Load* is remembered per build in `HKCU\Software\Autodesk\Revit\Autodesk Revit 2023\CodeSigning`
  under the add-in's ClientId); trusted signature → no dialog; self-signed/untrusted → "Security -
  Invalid Signature" (Load / Do Not Load, every start). `scripts/sign.ps1`, `package-release.ps1 -Sign
  -Thumbprint …` (refuses a self-signed signature without `-AllowSelfSigned`).

### Chat Pane (in-Revit chat)
The pane's **Claude Code** backend spawns the CLI headless per turn:
`claude -p <prompt> --output-format stream-json --verbose --permission-mode dontAsk --allowedTools Bash,PowerShell,Read,Grep,Glob [--resume <id>] [--add-dir <project folder>] --append-system-prompt <turn prompt>`.
`PowerShell`: on a laptop without Git for Windows the CLI has no Bash and shells out through its
PowerShell tool; the prompt file says to call `curl.exe` there (`ClaudeCodeBackend.PowerShellRule`).
`--permission-mode dontAsk` is deliberate: without it the CLI inherits the user's `defaultMode`,
and in `auto` mode the safety classifier refused approved `delete`/`select` curls as
"Irreversible Local Destruction". `dontAsk` makes `--allowedTools` the whole policy (Bash, Read and the read-only Grep/Glob
run, every other tool is denied, never prompted) — narrower than `bypassPermissions`, and unlike
`Bash(curl:*)` it keeps the `cat > /tmp/q.cs` + `exec --data-binary` pattern working. Every
VibeModel modification is one Ctrl+Z, which the prompt tells the model. The turn prompt ends with
the shared **RULES** block (`ChatBackendBase.BuildBehaviourRules(language)` — reply language,
undo, ask-first for sheets/shared resources, compact answers); edit the wording there, both
backends use it. Settings → *Taal / Language* (`Language`: `nl` default, `en`) sets the fallback
reply language and the pane's fixed texts (`Services/Chat/Strings.cs`, `StatusVerbs.cs`) —
the only language source and the only Dutch/English tables; `UI/UiText.cs` just composes them.

**Arguments go in `?args=`.** The prompt file the CLI reads (`ClaudeCodeBackend.BuildSystemPromptText`)
lists every command as `curl -s "http://127.0.0.1:<port>/<cmd>?args=ARGS"   ARGS: <grammar>` (the
grammar is the command's `Usage` minus its name) under a HOW TO CALL block with the rule
(`ClaudeCodeBackend.ArgsRule`, also repeated in every turn prompt), encoded examples,
`curl -G --data-urlencode "args=…"` for code, and `POST /batch --data-binary $'a\nb'`. A path that
carries its own args (`/wall%200%200%205000%200`) still runs (`RevitHttpServer.TrySplitPathArgs`).

**Activity block.** Each turn's tool calls show as live step rows under the question (thinking
indicator with mm:ss, ✓/✗ per step, intermediate text muted) and collapse to "N stappen · 48 s".
Backends report steps on a UI-only side channel (`IChatActivitySource.ActivitySink`,
`ChatActivityEvent`); `TurnActivity` / `ToolCallDescriber` (`Services/Chat/ChatActivity.cs`) are the
WPF-free model, `UI/ActivityBlockView.cs` draws it. The token stream, stored chat JSON and the
model-bound history are unchanged. A new command gets its step label from `StatusVerbs` (add it to
BOTH maps — a test checks).

**Tables, units, sources (RULES block).** The rules end with three more sections, each a constant
in `ChatBackendBase` (`TableRule`, `UnitsRule`/`UnitsConversionRule`, `SourcesRule`/`SourceFormsRule`):
a markdown table for >3 items with 2+ attributes (units in the header cell), a unit with every
quantity, and a `[bron: …]` marker after every fact taken from the office standards, the leidraad,
a project document or the model. Markdig runs with pipe + grid tables; `Markdown/Renderers/TableRenderer.cs`
draws them (layout decisions — numeric columns, 30 shown rows, TSV and the column widths — are the
WPF-free `Markdown/TableLayout.cs`). Widths use browser-style auto layout (`TableLayout.Fit`): one line
per cell when it fits, else every column keeps its longest word, and when even that does not fit the
text columns give way before the numeric ones; `ChatPane` refits on resize (`TableRenderer.FitTables`).
In the default ~300 px pane a 5-column table still breaks words, hence the rule's 4-column limit.

**Source markers.** `Markdown/Citations.cs` (`CitationParser`, WPF-free) finds
`[bron: <kind> <target>]` outside code spans/fences before Markdig parses, swaps each for a
private-use placeholder and numbers the unique sources; `CitationInlines` renders the superscript
and the collapsed "Bronnen (N)" footer. Kinds and what a click does (`Citation.TryGetLookup`, run by
the pane off the UI thread):
```
[bron: leidraad 4.2]              -> standards leidraad 4.2        (popup text)
[bron: standaard phasing]         -> standards phasing             (popup text)
[bron: document memo.pdf p.3]     -> readdoc memo.pdf              (scrolled to "--- page 3 ---"; §x -> first match)
[bron: revit 1543814]             -> select + zoomto 1543814       (a parameter name without id: tooltip only)
[bron: revit-api]                 -> tooltip only
```
Malformed markers (no colon, empty, unclosed, `\[bron:`) stay text. Display only: the stored chat
JSON and the model-bound history keep the raw markers.

**Welcome card.** An empty chat starts with `ChatPane.BuildWelcomeCard()`: backend + model
(`ChatBackendBase.DisplayName` / `ModelName` — for Claude Code the model of the last turn's init
event, else `ANTHROPIC_MODEL` / `~/.claude/settings.json`), the open model + project folder, and
up to four **starter chips** (`Services/Chat/Starters.cs`, no model call): "Wat heb ik geselecteerd?"
when something is selected, "Zet deze views passend op dit blad" on a sheet, "Controleer dit model
tegen de kantoorstandaard" with an active office pack, "Welke documenten zitten in de projectmap?"
when the folder has documents, then the fixed examples. The facts come from `ActiveDocumentTracker`
(active view, sheet flag and selection size — sampled on Idling at most every 400 ms, on view
activation and in every command's stamp) and a cached off-thread project-folder listing. The card
rebuilds in place after Settings closes and, while the chat is still empty, whenever a tracked fact
changes (`ActiveDocumentTracker.Changed`) — mid-conversation it stays put so the messages never
shift. Tapping a starter fills the input. The header's `?` shows the longer list
(`Strings.ExamplesList`). All texts are `Strings` keys (nl/en) composed by `UiText`.

**Quick replies** (`Services/Chat/QuickReplies/`, transferred from DegenAI's quick replies). After a
completed answer the pane makes a separate cheap call — never on the token stream — for none or two
to four one-tap chips above the input. Pre-filter (`QuickReplyService.ShouldOffer`): no call for an
answer under 40 characters, a cancelled turn or an error. Input: the last 6 *stored* messages (500
characters each — a question keeps its start, a stored answer its end, since it opens with the step
notes; the current turn excluded), the question (1000) and the displayed answer (3000);
the system prompt (`SystemPromptTemplate`, one constant) gets the active view, its phase (from the
TEMPLATE block), the selection size and the commands this turn ran (`TurnActivity.ToolNames`).
Model per backend (`SuggestionModels.For`): Anthropic API → `claude-haiku-5-5` with the schema as a
forced tool; Claude Code → `claude -p --model haiku --output-format json --json-schema … --tools ""
--no-session-persistence --effort low --strict-mcp-config --system-prompt …` (input on stdin, run
from `%LOCALAPPDATA%\VibeModel\quickreplies`; a CLI that rejects `--json-schema`/`--tools` is skipped
for the session, unknown speed-up flags are dropped one by one); local LLM → JSON mode with the schema
in the prompt, and only with tool use on (a chat-only model could not carry out any chip).
Timeout 8 s, 12 s for the CLI (its startup alone is 3–5 s; live calls took 3–7 s).
`ParseAndSanitize` never trusts the model: `offer:false` → none; a label over 44 or a prompt over
240 characters is dropped (not cut); decline labels ("Nee bedankt", "Laat maar", "Not now", …,
`DeclineLabels`, whole normalized label) are dropped and logged — a hit means the prompt regressed;
duplicates out; at most 4; fewer than 2 left → no row. UI: a plain chip sends its prompt at once; a
chip with `commits` (it changes the model) shows a pencil and fills the input with the hint "Klaar om
te versturen…"; the row steps aside 1 s after typing starts and returns on an empty box; a set that
arrives after a newer answer, another chat, a running turn or typing is dropped; New Chat, History,
a backend switch and sending clear it; chips are never stored in the chat JSON. Chips are Buttons
with AutomationIds `StarterChip` / `QuickReplyChip` / `QuickReplyChipCommit`. The raw model JSON is
logged (`Quick replies: … answered in N ms: {…}`, debug log).

### Successcase logbook (scripts)
Turn workshop follow-up questions into case files for the follow-up presentation — protocol in
`docs/successcases.md`:
```bash
python -I scripts/successcase_mail.py "<subject part>"                 # mail -> new case
python -I scripts/successcase_mail.py "<subject>" --chat <chat-id>     # link the reproducing chat
python -I scripts/successcase.py <chat-id>                             # case from a chat only
python -I scripts/successcase.py --index-only                          # rebuild INDEX.md
python -I -m unittest discover -s scripts/tests -v                     # tests
```
Cases go to `demo_out/successcases/` (gitignored — they contain colleagues' names).

### Fallback: File-Based (if HTTP unavailable)
If the HTTP server cannot start (port blocked), VibeModel falls back to file-based polling:
```bash
echo "selected" > "C:/RevitClaudeLink/command.tmp" && cmd /c move /Y "C:\\RevitClaudeLink\\command.tmp" "C:\\RevitClaudeLink\\command.txt"
sleep 2 && cat "C:/RevitClaudeLink/output.txt"
```

---

## Architecture

```
Claude Code (bash)                     Revit Add-in
─────────────────                      ────────────────
curl localhost:18884/selected ───────► RevitHttpServer (TcpListener, background thread)
                                       │ Parse HTTP request
                                       │ Enqueue CommandRequest
                                       │ Raise ExternalEvent
                                       │ Wait (ManualResetEvent, 30s timeout)
                                       │
                                       ├─► RevitCommandHandler (Revit main thread)
                                       │   │ Dequeue request
                                       │   │ Route via ClaudeCommandRegistry
                                       │   │ Execute IClaudeCommand
                                       │   └ Signal ManualResetEvent
                                       │
◄──────────────────────────────────────┘ Send HTTP response
```

### Adding a New Command
1. Create a new file in `Services/Claude/Commands/`
2. Implement `IClaudeCommand`
3. Build — the command is auto-discovered via reflection

```csharp
public class MyCommand : IClaudeCommand
{
    public string Name => "mycommand";
    public string Description => "Does something cool";
    public string Usage => "mycommand <arg>";

    public string Execute(string args, UIApplication uiApp)
    {
        return "Result: " + args;
    }
}
```

---

## Task Management with Ralphy

This project uses `ralphy` CLI for autonomous AI coding loops. Ralphy orchestrates AI agents (including Claude Code) to complete coding tasks.

### Usage

**Single Task Mode** - Execute a one-off instruction:
```
ralphy "add login button"
```

**Task List Mode** - Process multiple items from PRD.md:
```
ralphy --prd PRD.md
```

Or simply run `ralphy` (looks for PRD.md in current directory by default).

### PRD.md Format

Tasks are defined using markdown checkboxes:

```markdown
## Tasks
- [ ] create auth
- [ ] add dashboard
- [x] done task (skipped)
```

### Key Flags

- `--prd PATH`: Specify task source file
- `--fast`: Skip testing and linting
- `--max-retries N`: Set retry attempts
- `--dry-run`: Preview without execution
- `--parallel`: Run multiple agents simultaneously
- `--branch-per-task`: Create branches per task
- `--create-pr`: Generate pull requests

### Initialize Project Config

```
ralphy --init
```

This generates `.ralphy/config.yaml` with project settings, commands, and execution rules.

### More Info

https://github.com/michaelshimeles/ralphy
