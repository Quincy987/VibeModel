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

# Set parameter value
curl -s "http://localhost:18884/set?args=12345 Comments Hello+World"

# Color override (RGB 0-255)
curl -s "http://localhost:18884/color?args=12345 255 0 0"
curl -s "http://localhost:18884/color?args=12345 reset"

# Place family instance
curl -s "http://localhost:18884/place?args=Door Single-Flush 5000 2500"
curl -s "http://localhost:18884/placeid?args=67890 5000 2500"

# Join / cut geometry between two elements (with diagnostics on refusal)
curl -s "http://localhost:18884/join?args=12345 67890"            # JoinGeometryUtils.JoinGeometry
curl -s "http://localhost:18884/join?args=12345 67890 --switch"   # Flip which element wins the join
curl -s "http://localhost:18884/join?args=12345 67890 --cut"      # Solid-solid cut: A cuts B
curl -s "http://localhost:18884/unjoin?args=12345 67890"          # Remove join and/or cut

# Execute arbitrary C# code
curl -s "http://localhost:18884/exec?args=sb.AppendLine(doc.Title);"
```

`join` always reports both categories, whether the bounding boxes actually intersect, which API
it called, and — on refusal — Revit's own reason in plain language plus the workaround that does
work (extend the solids so they really overlap; use a void family or Parts when a solid-solid cut
is refused; `--switch` when the wrong element wins). Use `--cut` for penetrations (a pile through
a quay wall, an H-beam inside a concrete pile); plain `join` for shared faces (a wing wall against
a capstone). `unjoin` removes whichever relation exists — join, cut, or both.

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

### Office Standards (template / standards / phase / check / coords / library / loadfamily / linkcad)
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
