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

### Quick Start
```bash
curl -s http://localhost:18884/health          # Check connection
curl -s http://localhost:18884/help            # List all commands
curl -s http://localhost:18884/info            # Document info
curl -s http://localhost:18884/selected        # Inspect selected elements
```

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
```bash
curl -s -X POST http://localhost:18884/batch -d "wall 0 0 5000 0
wall 5000 0 5000 5000
wall 5000 5000 0 5000
wall 0 5000 0 0"
```

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
