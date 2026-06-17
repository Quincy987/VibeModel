# Plan 01 — Vision Feedback Loop

Give the AI the ability to **see** the Revit model it is editing: a new `/screenshot`
command that exports the active view to PNG, plus wiring so the vision-capable chat
backends inline that image back into the conversation after a change.

---

## 1. Chosen approach (summary)

**Return a file path, not bytes.** `/screenshot` exports the active view to a PNG on
disk and returns a small **plain-text** payload (absolute path + metadata). This keeps
the existing `text/plain` HTTP contract 100% intact — **zero changes to
`RevitHttpServer.cs`** and no response-contract refactor.

Each backend then consumes that path differently:

- **ClaudeCodeBackend** — *no code change needed for image input.* The Claude CLI
  already runs with `--allowedTools Bash,Read`, and its `Read` tool reads PNGs
  natively. We only update the system prompt to tell it to `Read` the returned path.
- **AnthropicDirectBackend** — special-case the `revit_screenshot` tool result: read
  the PNG, base64-encode it, and emit an Anthropic **image content block** inside the
  `tool_result`. This is the one place that needs real new logic.
- **LocalLlmBackend** — most local models aren't vision-capable. It returns the path as
  text only; no image inlining. Documented as a known limitation (optional future
  enhancement for llava-class models).

Rationale for path-over-bytes:
- The backends run **in-process inside Revit**, on the **same machine** — they can read
  the file directly. There is no network boundary to cross.
- Base64-in-body (~33% bloat) would flow through `WebClient.DownloadString` and the
  agent loop as a giant text blob; awkward and wasteful.
- A new binary HTTP path would mean teaching `SendResponse` about `Content-Type` and
  raw bytes, plus a second response model — unjustified for a same-machine reader.

> Optional extra (not required, flagged as open question): also expose a raw
> `image/png` binary endpoint so a human can `curl` the screenshot in a browser. This
> is purely a convenience and is **not** on the critical path.

---

## 2. Files to add / change

### NEW — `src/VibeModel/Services/Claude/Commands/ScreenshotCommand.cs`

Implements `IClaudeCommand`, auto-discovered by `ClaudeCommandRegistry` (reflection) —
no registration wiring needed. Modeled on `ActiveViewCommand` (active-view resolution)
and `ZoomToCommand` (UIDocument usage).

Responsibilities:
1. Resolve the active graphical view (same pattern as `ActiveViewCommand`:
   `ActiveGraphicalView` → fall back to `ActiveView` → error if none).
2. Reject non-exportable view types early (schedules, legends, sheets-as-text, etc.).
3. Build `ImageExportOptions`, call `doc.ExportImage(...)`, locate the produced PNG.
4. Return a plain-text payload with the absolute path + metadata.

Sketch:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class ScreenshotCommand : IClaudeCommand
    {
        public string Name => "screenshot";
        public string Description => "Export the active view to a PNG so it can be viewed";
        public string Usage => "screenshot [pixels]";   // optional long-edge px, default 1536

        // Vision sweet spot: big enough to read geometry, small enough to keep tokens sane.
        private const int DefaultPixels = 1536;
        private const int MinPixels = 512;
        private const int MaxPixels = 3000;

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            View view;
            try { view = uiDoc.ActiveGraphicalView; } catch { view = null; }
            if (view == null) view = uiDoc.ActiveView;
            if (view == null) return "ERROR: No active view to capture.";

            // Guard view types Revit can't raster-export as a single image.
            if (view is ViewSchedule || view.ViewType == ViewType.Legend ||
                view.ViewType == ViewType.DrawingSheet)
                return "ERROR: View type '" + view.ViewType + "' cannot be exported as an image. " +
                       "Switch to a plan, section, elevation, or 3D view.";

            int pixels = DefaultPixels;
            if (!string.IsNullOrWhiteSpace(args) &&
                int.TryParse(args.Trim(), out var p))
                pixels = Math.Max(MinPixels, Math.Min(MaxPixels, p));

            // Export into a fresh, empty temp dir so we can unambiguously find the PNG.
            // (Revit mangles the filename — see "Filename mangling" note below.)
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel", "screenshots", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var basePath = Path.Combine(dir, "view");

            var opts = new ImageExportOptions
            {
                FilePath = basePath,
                ExportRange = ExportRange.CurrentView,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,                 // long-edge pixels when FitToPage
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };

            try
            {
                doc.ExportImage(opts);   // read-only — no Transaction required
            }
            catch (Exception ex)
            {
                return "ERROR: ExportImage failed: " + ex.Message;
            }

            // Revit appends "<ViewType> <ViewName>.png" to FilePath, so glob the dir.
            var png = Directory.GetFiles(dir, "*.png").FirstOrDefault();
            if (png == null)
                return "ERROR: ExportImage produced no PNG (view may be empty or hidden).";

            var info = new FileInfo(png);
            var sb = new StringBuilder();
            sb.AppendLine("SCREENSHOT");
            sb.AppendLine("==========");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("Pixels (long edge): " + pixels);
            sb.AppendLine("Size: " + (info.Length / 1024) + " KB");
            sb.AppendLine("Path: " + png);
            sb.AppendLine();
            sb.AppendLine("The PNG at the path above shows the active view. " +
                          "Open/read it to see the current state of the model.");
            return sb.ToString();
        }
    }
}
```

Key API notes (`ImageExportOptions` model, stable across Revit 2020–2024; project targets
`net48`, Revit 2023 default per `build.ps1`):
- `ExportRange.CurrentView` exports exactly the active view — no sheet/set juggling.
- `ZoomFitType.FitToPage` + `PixelSize` = the long-edge pixel count drives output size,
  which is what controls vision token cost. (If `ZoomType=Zoom`, `Zoom` is a % instead.)
- `ExportImage` is a **read-only** operation — safe to run on the main thread via the
  existing `RevitCommandHandler` ExternalEvent with no `Transaction`.

### CHANGE — `src/VibeModel/Services/Chat/AnthropicDirectBackend.cs`

This is the only backend needing real image-wiring. Today every tool result is sent as
a **string** (`AnthropicDirectBackend.cs:295-300`):

```csharp
toolResults.Add(new Dictionary<string, object>
{
    { "type", "tool_result" },
    { "tool_use_id", toolId },
    { "content", result }      // <-- plain string
});
```

Anthropic's API also accepts `content` as an **array of content blocks**, including
image blocks. We special-case `revit_screenshot`: parse the `Path:` line out of the
text result, read + base64 the file, and build a mixed text+image content array.

```csharp
// New helper in AnthropicDirectBackend
private object BuildToolResultContent(string toolName, string result)
{
    // Only the screenshot tool returns an image-bearing path.
    if (toolName == "revit_screenshot" && !result.StartsWith("ERROR"))
    {
        var path = ExtractPath(result);   // parse the "Path: ..." line
        if (path != null && File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var b64 = Convert.ToBase64String(bytes);
                return new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "type", "text" },
                        { "text", result }
                    },
                    new Dictionary<string, object>
                    {
                        { "type", "image" },
                        { "source", new Dictionary<string, object>
                            {
                                { "type", "base64" },
                                { "media_type", "image/png" },
                                { "data", b64 }
                            }
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Screenshot inline failed", ex);
                return result + "\n[Could not attach image: " + ex.Message + "]";
            }
        }
    }
    return result;   // default: plain string, unchanged behavior
}

private static string ExtractPath(string result)
{
    foreach (var line in result.Split('\n'))
        if (line.StartsWith("Path:", StringComparison.OrdinalIgnoreCase))
            return line.Substring(5).Trim();
    return null;
}
```

Then the tool-result construction becomes:

```csharp
toolResults.Add(new Dictionary<string, object>
{
    { "type", "tool_result" },
    { "tool_use_id", toolId },
    { "content", BuildToolResultContent(toolName, result) }
});
```

`System.IO` is already imported (`AnthropicDirectBackend.cs:3`). `JavaScriptSerializer`
will serialize the nested arrays/dicts correctly (it already does for `tools`).

Also add a one-line nudge in `BuildSystemPrompt()` (around line 388):
> "Use revit_screenshot to SEE the active view — after a visual change, take a
> screenshot to verify the result looks right before reporting done."

### CHANGE — `src/VibeModel/Services/Chat/ClaudeCodeBackend.cs`

No image-plumbing code required — the CLI's `Read` tool ingests PNGs natively and
`Read` is already in `--allowedTools Bash,Read` (`ClaudeCodeBackend.cs:300`). Two small
prompt updates so the model knows to use it:

1. In `WriteSystemPrompt(...)` Tips section (around line 146), add:
   > "- To SEE the model, run the screenshot command, then use your Read tool on the
   >   returned Path to view the PNG."
2. In the per-request `appendPrompt` (around line 321), add the same hint so it's salient
   each turn:
   > "After a visual change, screenshot the active view and Read the PNG to verify."

### CHANGE — `src/VibeModel/Services/Chat/LocalLlmBackend.cs` (minimal)

No image wiring. The screenshot tool result flows through as plain text (the path), which
a non-vision model can't open. Add a code comment + a single system-prompt line noting the
model can call `screenshot` but only a vision-capable local model could use the result.
(Full llava-style base64 wiring is out of scope — see open questions.)

---

## 3. Binary-response decision (explicit)

**Decision: do NOT add a binary HTTP path. Return a file path in the existing
`text/plain` body.**

- `RevitHttpServer.SendResponse` hardcodes `Content-Type: text/plain; charset=utf-8` and
  takes a `string` body (`RevitHttpServer.cs:320-336`). Returning bytes would require a
  second response model and content-type plumbing.
- Both vision backends run **in-process, same machine** → direct file read is trivial and
  cheaper than shipping base64 through the agent loop.
- Net result: `RevitHttpServer.cs`, `RevitCommandHandler.cs`, `IClaudeCommand`, and
  `ClaudeCommandRegistry` are **untouched**. The command returns a string like every
  other command — fully compatible with the current "commands return raw strings"
  contract. **No response-contract refactor is needed for this feature.**

---

## 4. Edge cases & failure modes

- **No active view** → `ERROR: No active view to capture.`
- **Non-exportable view** (schedule / legend / sheet) → early `ERROR` with guidance to
  switch views. (`ExportImage` on these either throws or yields nothing useful.)
- **Filename mangling** — Revit appends `" - <ViewType> - <ViewName>.png"` (or similar)
  to `FilePath`; the exact suffix isn't predictable. Mitigation: export into a fresh
  GUID temp dir and glob `*.png` for the single produced file.
- **Empty / all-hidden view** → no PNG produced → `ERROR: ExportImage produced no PNG`.
- **`ExportImage` throws** (locked file, bad options, view in editing mode) → caught,
  returned as `ERROR: ExportImage failed: <msg>`.
- **Image too large for tokens** — capped via `PixelSize` (default 1536, max 3000). Note
  Anthropic resizes images > ~1568px long edge anyway; our default sits just under that.
- **Disk growth** — each shot lands in its own temp dir under
  `%LOCALAPPDATA%\VibeModel\screenshots\`. Add a best-effort cleanup: on command start,
  delete screenshot subdirs older than N (e.g. keep last ~20 or prune > 1h). (Low risk;
  can be a follow-up.)
- **Anthropic base64 size limit** — API caps a single image at ~5 MB base64. A 1536px
  PNG is well under that; the `MaxPixels=3000` cap keeps us safe.
- **Windows path parsing** — `ExtractPath` only strips the `Path:` prefix then `Trim()`s
  the remainder, so a `C:\...` drive-letter path survives intact.

---

## 5. Testing / verification (Revit must be running)

Build & restart via the project script (never ask the user to close Revit manually):

```powershell
.\build.ps1 -Debug      # Revit 2023, debug logging on
```

**A. Command-level (curl, no AI):**
```bash
curl -s http://localhost:18884/health                 # confirm server up
curl -s http://localhost:18884/screenshot             # default 1536px
curl -s "http://localhost:18884/screenshot?args=1024" # custom size
```
Expect a `SCREENSHOT` text block with a `Path:` line. Open the printed PNG and confirm it
matches the active Revit view. Switch to a schedule view and re-run → expect the guarded
`ERROR`.

**B. AnthropicDirectBackend (vision inlining):**
- In the VibeModel chat, with an Anthropic API key set, ask: *"Take a screenshot and
  describe what you see."*
- Check `%LOCALAPPDATA%\VibeModel\logs\` for the outgoing request log line — confirm the
  `tool_result` carries an `image` block (base64), not just text.
- Confirm the model's reply describes actual on-screen geometry (proves the image landed).
- Closed-loop test: *"Create a wall from 0,0 to 5000,0, then screenshot and tell me if it
  looks right."* → wall creation → screenshot → visual confirmation.

**C. ClaudeCodeBackend:**
- With the CLI available, ask the same screenshot question. Confirm in logs that the CLI
  invoked its `Read` tool on the PNG path and that the answer reflects the image.

**D. Regression:**
- Run a few existing text commands (`/info`, `/selected`, `/list?args=walls`) through both
  backends to confirm the `BuildToolResultContent` string-default path is unchanged.

---

## 6. Open questions for the user

1. **Default resolution** — 1536px long edge (≈ Anthropic's resize threshold, good
   token/clarity balance). Different default or higher cap?
2. **Auto-screenshot after modifications?** — Should modification commands (wall, floor,
   place, color) *automatically* trigger a screenshot for the AI, or only when the AI
   explicitly asks? (Auto = more tokens every turn; explicit = AI must remember.) Lean
   **explicit**, nudged by the system prompt.
3. **Local LLM vision** — Leave LocalLlmBackend text-only (recommended), or add optional
   base64 wiring for vision models like llava when `GetLocalLlmModel()` looks vision-capable?
4. **Human convenience endpoint** — Want a raw `image/png` HTTP endpoint too (so a person
   can `curl`/browser-view the shot)? Adds the only change to `RevitHttpServer.cs`;
   otherwise that file stays untouched.
5. **Screenshot cleanup** — OK to prune old screenshot temp dirs automatically (keep last
   ~20), or leave them for the user to clear?
