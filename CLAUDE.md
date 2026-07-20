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
```

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

# Execute arbitrary C# code
curl -s "http://localhost:18884/exec?args=sb.AppendLine(doc.Title);"
```

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
