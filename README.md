# VibeModel

Talk to your Revit model in plain English. VibeModel is a Revit add-in that lets you use AI (Claude) to inspect and modify your model through a simple chat interface.

<!-- TODO: Add screenshot/demo GIF here -->

## What You Need

- **Autodesk Revit** (2022+)
- **An Anthropic API key** — get one from [console.anthropic.com](https://console.anthropic.com/)
- **Claude Code** (recommended) — provides full agentic capabilities (bash, file access, context management)

### Installing Claude Code (recommended)

1. Install [Node.js](https://nodejs.org/) (download the LTS version and run the installer)
2. Open a terminal (press `Win + R`, type `cmd`, press Enter) and run:
   ```
   npm install -g @anthropic-ai/claude-code
   ```
3. Set your API key:
   ```
   setx ANTHROPIC_API_KEY sk-ant-...your-key-here...
   ```

VibeModel will automatically detect Claude Code when Revit starts. Claude Code gives you the best experience — context window management, token tracking, file access, and bash execution.

**Don't want to install Node.js?** You can skip Claude Code and use the built-in direct API mode instead. Click **Settings** in the chat panel to enter your API key. This works but has some limitations (no context window management, no cost tracking).

## Installation

### Easy way (recommended)

1. **Download** the latest release from [GitHub Releases](https://github.com/Quincy987/VibeModel/releases)
2. **Unzip** the downloaded file
3. **Run `install.ps1`** (right-click → "Run with PowerShell", or from a terminal: `.\install.ps1`)
   - It auto-detects your Revit version(s) and copies the files
   - Optionally saves an API key for direct mode (you can skip this if using Claude Code)
4. **Open Revit** — you should see a new **VibeModel** tab in the ribbon

### Manual installation

1. **Download** the latest release and unzip — you should see three files:
   - `VibeModel.dll`
   - `VibeModel.addin`
   - `Markdig.dll`
2. **Copy all three files** into your Revit add-ins folder:
   ```
   %APPDATA%\Autodesk\Revit\Addins\{year}\
   ```
   Replace `{year}` with your Revit version number (e.g., `2023`, `2024`, or `2025`).

   **How to find this folder:** Open File Explorer, click the address bar at the top, paste the path above (with your year), and press Enter. If the folder doesn't exist, create it.

3. **Restart Revit** — you should see a new **VibeModel** tab in the ribbon

## Usage

1. Open a Revit project
2. Click the **Chat** button on the **VibeModel** tab in the ribbon
3. A chat panel opens on the side — type a message and Claude will respond

### Things you can say

- "What's selected?"
- "List all the walls"
- "Show me the document info"
- "Color the selected element red"
- "Create a wall from 0,0 to 5000,0 that's 3000mm high"
- "Delete element 12345"
- "What are the parameters of the selected element?"

### Using from the command line

VibeModel also works from any terminal. You can send commands directly:

```bash
curl -s http://localhost:18884/health          # Check connection
curl -s http://localhost:18884/help            # List all commands
curl -s http://localhost:18884/selected        # Inspect selected elements
curl -s http://localhost:18884/info            # Document info
```

## Available Commands

| Command | What it does | Example |
|---------|-------------|---------|
| `health` | Check if VibeModel is running | `curl -s http://localhost:18884/health` |
| `help` | List all available commands | `curl -s http://localhost:18884/help` |
| `info` | Show document info | `curl -s http://localhost:18884/info` |
| `selected` | Show details of selected elements | `curl -s http://localhost:18884/selected` |
| `list` | List elements by category | `curl -s "http://localhost:18884/list?args=walls"` |
| `get` | Get element by ID | `curl -s "http://localhost:18884/get?args=12345"` |
| `wall` | Create a wall (mm) | `curl -s "http://localhost:18884/wall?args=0 0 5000 0 3000"` |
| `floor` | Create a floor from points (mm) | `curl -s "http://localhost:18884/floor?args=0,0 5000,0 5000,5000 0,5000"` |
| `delete` | Delete elements | `curl -s "http://localhost:18884/delete?args=12345"` |
| `set` | Set a parameter value | `curl -s "http://localhost:18884/set?args=12345 Comments Hello"` |
| `color` | Color an element (RGB) | `curl -s "http://localhost:18884/color?args=12345 255 0 0"` |
| `place` | Place a family instance | `curl -s "http://localhost:18884/place?args=Door Single-Flush 5000 2500"` |
| `params` | Show parameters of selected element | `curl -s http://localhost:18884/params` |
| `views` | List all views | `curl -s http://localhost:18884/views` |
| `levels` | List all levels | `curl -s http://localhost:18884/levels` |

For the full list of commands, run `curl -s http://localhost:18884/help` or see [CLAUDE.md](CLAUDE.md).

## Building from Source

This section is for developers who want to modify VibeModel.

1. Clone the repository
2. Run the build script:
   ```powershell
   .\build.ps1                      # Default: Revit 2023
   .\build.ps1 -RevitVersion 2024   # Target a specific version
   .\build.ps1 -Debug               # Enable debug logging
   ```
   The script will close Revit, build, copy the files to the right folder, and restart Revit automatically.

## Troubleshooting

**VibeModel tab doesn't appear in Revit**
- Make sure all three files (`VibeModel.dll`, `VibeModel.addin`, `Markdig.dll`) are in the correct folder
- Double-check the year in the folder path matches your Revit version
- Try restarting Revit

**Chat not connected**
- **With Claude Code (recommended):** Make sure Claude Code is installed (`npm install -g @anthropic-ai/claude-code`) and your `ANTHROPIC_API_KEY` environment variable is set. Try running `claude --version` in a terminal to verify.
- **With direct API mode:** Click **Settings** in the chat panel header and enter your Anthropic API key. Get a key from [console.anthropic.com](https://console.anthropic.com/).

**Connection refused / port blocked**
- VibeModel uses port 18884 — make sure nothing else is using it
- If the port is blocked, VibeModel automatically falls back to file-based mode (slower but still works)
- Check the logs at `%LOCALAPPDATA%\VibeModel\logs\` for details

**Commands return errors**
- Make sure you have a Revit project open (not just the start screen)
- Check that the element IDs you're referencing actually exist in your model

## License
Mozilla Public License v2
