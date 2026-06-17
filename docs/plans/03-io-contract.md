# Plan 03 — I/O Contract (Structured JSON Output + Self-Correcting Errors)

## Goal

Two halves of one feature, sharing a single refactor:

1. **Structured JSON output mode** — commands can return machine-parseable JSON
   (opt-in via `?format=json` query param or `Accept: application/json` header), so the
   agentic tool loop reads fields instead of scraping formatted text. **Text remains the
   DEFAULT** — human curl and existing AI prompts are byte-for-byte unaffected.
2. **Self-correcting structured errors** — replace flat `ERROR: <msg>` with a structured
   error carrying a `code`, a human `message`, and a `suggestion`/next-action so the AI
   self-recovers instead of looping. In text mode the suggestion is appended as an
   additive `Hint:` line; the machine `code` appears only in JSON.

Both reduce to the same foundation: a **command response contract** (`CommandResult`).

---

## 1. The decisive discovery (shapes the whole plan)

The chat backends **do not call the registry directly**. They invoke commands over HTTP
loopback and read the text body:

- `AnthropicDirectBackend.cs:332` — `var url = "http://localhost:" + _httpPort + "/" + commandName; if (args) url += "?args=" + Uri.EscapeDataString(args);`
- `LocalLlmBackend.cs:397` — identical loopback pattern.
- `ClaudeCodeBackend` — spawns the Claude Code CLI, which curls the documented endpoints itself.

**Consequences:**
- Enabling JSON for a backend = appending `&format=json` to its loopback URL. No registry
  plumbing on the chat side.
- The format flag only has to be threaded through the **HTTP → handler → registry → command**
  path *once* (§4).
- Structured text errors (the `Hint:` line) flow back to **all three backends in text mode
  today**, via the `tool_result` they already feed the model — instant self-correction with
  zero backend changes. This is the highest-value, lowest-risk slice.

---

## 2. Current contract (with refs)

### The interface returns `string`
`src/VibeModel/Services/Claude/IClaudeCommand.cs:24`
```csharp
string Execute(string args, Autodesk.Revit.UI.UIApplication uiApp);
```
33 commands implement this; every one returns formatted text, and signals failure by
returning a string starting with `"ERROR:"`. `IModificationCommand` (`:31`) is a marker
used to distinguish write vs read commands.

### The registry wraps results and errors
`src/VibeModel/Services/Claude/ClaudeCommandRegistry.cs:27`
```csharp
public string Execute(string command, string args, UIApplication uiApp)
{
    if (_commands.TryGetValue(command, out var cmd))
    {
        if (!NoDocRequired.Contains(command) && uiApp.ActiveUIDocument?.Document == null)
            return "ERROR: No document open";                                   // :33
        try { return cmd.Execute(args, uiApp); }                                 // :37
        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
        { ...; return "ERROR: Revit operation failed: " + ex.Message; }          // :43
        catch (Exception ex)
        { ...; return "ERROR: Command '" + command + "' failed: " + ex.Message; }// :48
    }
    return "ERROR: Unknown command '" + command + "'\n\n...";                    // :52
}
```

### The request path (where a format flag must ride along)
- `RevitHttpServer.ReadHttpRequest` (`:150`) parses the request line and **discards every
  header except `Content-Length`**. `Accept` is not captured today.
- `RevitHttpServer.ProcessRequest` (`:240`) splits `path?query`, extracts args via
  `ParseQueryArgs` (`:306`, reads only `args=`), then calls
  `_commandHandler.EnqueueAndWait(path, args)` (`:276`).
- `RevitCommandHandler.EnqueueAndWait` (`:44`) builds a `CommandRequest`, raises the
  `ExternalEvent`, blocks, and returns `request.Result`. `Execute` (`:87`) drains the queue
  on the main thread and calls `_registry.Execute(request.Command, request.Args, app)` (`:103`).
- `RevitHttpServer.SendResponse` (`:320`) **hardcodes** `Content-Type: text/plain; charset=utf-8` (`:326`).

### Error convention blast radius
- The `"ERROR:"` string prefix is the project-wide success/failure signal.
- `TransactionHelper.Execute` (`TransactionHelper.cs:16`) returns `null` on success,
  `"ERROR: " + ex.Message` on failure (`:36`).
- **Plan 02** (`docs/plans/02-transaction-integrity.md`) adds `ExecuteBatch`, which checks
  `result.StartsWith("ERROR")` (§4a, line 121) and `_registry.IsModification(cmd)` to decide
  Assimilate vs RollBack. Any error change must keep `StartsWith("ERROR")` true in text mode.

### JSON serializer already present
`AnthropicDirectBackend.cs:24` — `new JavaScriptSerializer { MaxJsonLength = int.MaxValue }`.
Reuse this pattern. Commit `ae1dda2` fixed an `ArrayList`-from-`JavaScriptSerializer` bug
(deserialize side); on the serialize side we avoid it by keeping payloads as
`Dictionary<string,object>` / `List<object>` — never `ArrayList`.

---

## 3. Chosen approach — Option (a), incremental via adapter

Introduce a `CommandResult` type and an **optional** richer interface `IStructuredCommand`,
while keeping `IClaudeCommand.Execute(string)` as the universal fallback. Migration becomes
**per-command, not big-bang**.

### Why (a) over (b)
Option (b) — "keep `string` returns, emit JSON only when asked" — still requires threading the
format flag down to *every* command (each command must know to emit JSON), so it saves none
of the plumbing. It also scatters ad-hoc JSON construction into 33 files with no shared
envelope and no uniform error shape. Option (a) centralizes rendering, gives one
success/error envelope, and — crucially — the **adapter** removes its only real downside:
blast radius. Unmigrated commands keep returning `string` and are wrapped automatically.

### Contract types (new)
`src/VibeModel/Services/Claude/CommandResult.cs` (new file):
```csharp
public enum ResponseFormat { Text, Json }

public sealed class CommandResult
{
    public bool   Success   { get; private set; }
    public object Data      { get; private set; }   // Dictionary<string,object> / List<object> — never ArrayList
    public string Text      { get; private set; }   // human form (legacy parity)
    public string ErrorCode { get; private set; }   // e.g. "TYPE_NOT_LOADED"; null on success
    public string Message   { get; private set; }   // human error message
    public string Suggestion{ get; private set; }   // self-correction hint; optional

    public static CommandResult Ok(string text, object data = null)
        => new CommandResult { Success = true, Text = text, Data = data };

    public static CommandResult Error(string code, string message, string suggestion = null)
        => new CommandResult { Success = false, ErrorCode = code, Message = message, Suggestion = suggestion };

    public string Render(ResponseFormat fmt, JavaScriptSerializer json)
    {
        if (fmt == ResponseFormat.Text)
        {
            if (Success) return Text ?? "";
            // Keep the "ERROR:" prefix (plan 02 + all scrapers depend on it).
            // Append an ADDITIVE Hint line — helps text-mode AI self-correct.
            var t = "ERROR: " + Message;
            if (!string.IsNullOrEmpty(Suggestion)) t += "\nHint: " + Suggestion;
            return t;
        }
        // JSON envelope
        object payload = Success
            ? (object)new Dictionary<string, object> {
                { "ok", true },
                { "data", Data },
                { "text", Data == null ? Text : null }   // text fallback only when no structured data
              }
            : new Dictionary<string, object> {
                { "ok", false },
                { "error", new Dictionary<string, object> {
                    { "code", ErrorCode }, { "message", Message }, { "suggestion", Suggestion } } }
              };
        return json.Serialize(payload);
    }
}
```

### Optional richer interface (for migration)
`IClaudeCommand.cs` (add alongside the existing interface):
```csharp
public interface IStructuredCommand
{
    CommandResult ExecuteStructured(string args, Autodesk.Revit.UI.UIApplication uiApp);
}
```
A command opts in by implementing `IStructuredCommand` *in addition to* `IClaudeCommand`
(the legacy `Execute` can delegate to `ExecuteStructured(...).Render(ResponseFormat.Text, ...)`
so there is exactly one code path producing its text).

### Registry routing (the single decision point)
`ClaudeCommandRegistry.Execute` gains a `ResponseFormat` parameter:
```csharp
public string Execute(string command, string args, UIApplication uiApp, ResponseFormat fmt)
{
    if (!_commands.TryGetValue(command, out var cmd))
        return CommandResult.Error("UNKNOWN_COMMAND",
            "Unknown command '" + command + "'", "Run 'help' to list commands.")
            .Render(fmt, _json);

    if (!NoDocRequired.Contains(command) && uiApp.ActiveUIDocument?.Document == null)
        return CommandResult.Error("NO_DOCUMENT",
            "No document open", "Open a Revit project before running commands.")
            .Render(fmt, _json);

    try
    {
        if (cmd is IStructuredCommand sc)
            return sc.ExecuteStructured(args, uiApp).Render(fmt, _json);

        // Legacy string command — adapt.
        var text = cmd.Execute(args, uiApp);
        if (fmt == ResponseFormat.Text) return text;                 // byte-identical to today
        return WrapLegacyText(text);                                 // uniform JSON envelope
    }
    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
    {
        Logger.Error("Revit operation failed for '" + command + "'", ex);
        return CommandResult.Error("REVIT_ERROR", "Revit operation failed: " + ex.Message,
            "Revit may be mid-operation (modal dialog/view switch); retry in a moment.").Render(fmt, _json);
    }
    catch (Exception ex)
    {
        Logger.Error("Command '" + command + "' failed", ex);
        return CommandResult.Error("INTERNAL", "Command '" + command + "' failed: " + ex.Message, null)
            .Render(fmt, _json);
    }
}

// Wrap an unmigrated command's text into the JSON envelope, detecting the ERROR convention.
private string WrapLegacyText(string text)
{
    if (text != null && text.StartsWith("ERROR", StringComparison.Ordinal))
        return CommandResult.Error("INTERNAL", text.Substring(text.IndexOf(':') >= 0 ? text.IndexOf(':') + 2 : 5).Trim(), null)
            .Render(ResponseFormat.Json, _json);
    return CommandResult.Ok(text).Render(ResponseFormat.Json, _json);   // {"ok":true,"text":"...","data":null}
}
```
The registry holds one `private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };`.

> **Backward-compat guarantee:** in `ResponseFormat.Text`, legacy commands return
> `cmd.Execute(...)` verbatim — the output is byte-identical to today. Migrated
> (`IStructuredCommand`) commands MUST render text identical to their old output (verified
> by diff in §9). Default format is `Text`, so no caller sees a change unless they opt in.

---

## 4. Threading `?format=json` through the path

1. **Capture `Accept`.** In `ReadHttpRequest` (`RevitHttpServer.cs:150`), while scanning
   header lines, capture the `Accept:` value into the `HttpRequest` (add an `Accept` field).
2. **Decide format.** In `ProcessRequest` (`:240`):
   ```csharp
   bool wantsJson = QueryHasFormatJson(query)                        // ?format=json
                 || (request.Accept != null &&
                     request.Accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0);
   var fmt = wantsJson ? ResponseFormat.Json : ResponseFormat.Text;
   ```
   `ParseQueryArgs` already reads only `args=`, so a `format=json` param is ignored for arg
   parsing — no conflict. Add a small `QueryHasFormatJson` that scans `&`-split pairs for
   `format=json`.
   - **Curl default `Accept: */*` must NOT trigger JSON** — match only an explicit
     `application/json` substring. Text stays the default.
3. **Pass it down.** `EnqueueAndWait(path, args, fmt)` → store `Format` on `CommandRequest`
   → `_registry.Execute(cmd, args, app, request.Format)` in `RevitCommandHandler.Execute`
   (`:103`). `CommandRequest` gets a `public ResponseFormat Format { get; }` set via the
   constructor (default `Text` for the batch/legacy paths that don't set it).
4. **Set Content-Type.** `SendResponse` (`:320`) takes a `contentType` argument;
   `ProcessRequest` returns it alongside the body (extend the private `HttpResponse` with a
   `ContentType`). Emit `application/json; charset=utf-8` when `fmt == Json`, else the current
   `text/plain; charset=utf-8`.

### Batch (`POST /batch`) under JSON
- `?format=json` on the POST URL applies to the whole batch.
- The batch envelope in JSON becomes an array of per-command result objects:
  `{"ok": <all-ok>, "results": [ {cmd, args, ...result...}, ... ]}`.
- This is where Plan 02 coordination matters (§5).

---

## 5. Coordination with Plan 02 (`StartsWith("ERROR")`)

Plan 02's `ExecuteBatch` (in `RevitCommandHandler`) calls `_registry.Execute(cmd, args, app)`
and branches on `result.StartsWith("ERROR")` + `_registry.IsModification(cmd)` to decide
Assimilate vs RollBack.

- **Text mode stays compatible unconditionally:** structured errors still render
  `ERROR: <message>` (with an additive `Hint:` line that still satisfies `StartsWith("ERROR")`).
- **Two clean landing orders:**
  - *02 first:* 03 updates `ExecuteBatch` to call the new `Execute(..., fmt)` overload and, in
    JSON mode, to branch on a `CommandResult.Success` rather than string-sniffing. For this,
    add a registry overload that returns the `CommandResult` object (not yet rendered) so the
    batch can both decide Assimilate/RollBack *and* assemble the JSON array:
    `CommandResult ExecuteCore(string command, string args, UIApplication uiApp)` — the
    string `Execute(...)` becomes a thin `ExecuteCore(...).Render(fmt, _json)` wrapper.
  - *03 first:* 02 builds on `ExecuteCore`/`CommandResult.Success` from the start, no
    string-sniffing introduced.
- Either way, `IsModification` (plan 02 §4c) is unaffected — it keys off the
  `IModificationCommand` marker, not the result string.
- **Recommendation:** land 03's `CommandResult` + `ExecuteCore` foundation first (or jointly),
  so 02's batch logic never has to depend on `StartsWith("ERROR")`. If 02 ships earlier, the
  `StartsWith("ERROR")` check remains correct and 03 swaps it to `.Success` as part of this plan.

---

## 6. Error-code taxonomy

`code` (machine, JSON only) · where it fires · example `suggestion`:

- `NO_DOCUMENT` — registry doc guard (`ClaudeCommandRegistry.cs:32`) — "Open a Revit project before running commands."
- `UNKNOWN_COMMAND` — command not in registry (`:52`) — "Run 'help' to list available commands."
- `BAD_ARGS` — missing/malformed args (e.g. `ListCommand.cs:24`, `WallCommand` coord parse) — the command's `Usage` string.
- `ELEMENT_NOT_FOUND` — `GetCommand` / `SelectCommand` with an ID not in the model — "Run 'list <category>' to find valid element IDs."
- `TYPE_NOT_LOADED` — `PlaceCommand` family/type not loaded — "Run 'familytypes' to see loaded types, or load the family first."
- `NO_ACTIVE_VIEW` — view-scoped ops with no graphical view (`ListCommand.cs:44`, `activeview`) — "Switch to a plan, section, or 3D view."
- `CATEGORY_NOT_FOUND` — `FormattingHelper.ResolveCategory` failure — "Run 'categories' to list valid categories."
- `TRANSACTION_FAILED` — `TransactionHelper.cs:36` rollback — "The model rejected the change; check geometry/constraints."
- `REVIT_ERROR` — `Autodesk.Revit.Exceptions.InvalidOperationException` (`:43`) — "Revit may be mid-dialog or switching views; retry in a moment."
- `EXEC_COMPILE_ERROR` — `ExecCommand` dynamic-compile failure — "Fix the C# snippet; check the compiler message in the response."
- `TIMEOUT` — command timeout (`RevitCommandHandler.cs:72`) — "Revit didn't respond in 30s; it may be in a modal dialog."
- `QUEUE_FULL` — queue cap (`:50`) — "Too many pending commands; wait and retry."
- `SHUTTING_DOWN` — disposing (`:47`, `:63`) — (no recovery; informational)
- `INTERNAL` — generic uncaught (`:48`) / wrapped legacy ERROR — (no specific hint)

**Text rendering rule:** `ERROR: <message>` always; append `\nHint: <suggestion>` only when a
suggestion exists. The `code` never appears in text — text stays essentially unchanged
(one optional added line), preserving every existing scraper and `StartsWith("ERROR")` check.

---

## 7. Incremental per-command migration

Migrate the **scrape-heavy query commands** first (these are what the agentic loop parses):

1. `info` (`InfoCommand.cs`) — emit `{title, path, isFamily, isWorkshared, phases:[...], counts:{...}}`.
2. `list` (`ListCommand.cs`) — emit `{category, view, total, truncated, elements:[{id, name, type, mark, family, symbol}]}`.
3. `selected` — `{count, elements:[...]}`.
4. `get` — single element object.
5. `params` — `{elementId, parameters:[{name, value, type, isReadOnly}]}`.
6. `levels`, `views` — arrays of `{id, name, ...}`.

For each: implement `IStructuredCommand.ExecuteStructured` returning `CommandResult.Ok(text, data)`,
and have the legacy `Execute` delegate to it for the text form (single source of truth).

Modification commands (`wall`, `floor`, `set`, `place`, `delete`, `color`, …) and `exec`
stay text-wrapped initially — their JSON value is lower (a success/ID line) and they can be
migrated later to emit `{created:[ids], modified:[ids]}`.

---

## 8. Edge cases

- **`Accept: */*` (curl default)** must not trigger JSON — match explicit `application/json` only.
- **`format=json` and `args=` coexist** — `ParseQueryArgs` reads only `args=`, so order/presence of `format` is irrelevant to arg parsing.
- **POST single command with `?format=json`** — format comes from the query string on the POST URL; body is still used as args (`RevitHttpServer.cs:271`).
- **Batch + JSON** — whole-batch flag; results assembled as a JSON array (§4). Default (text) batch output unchanged.
- **Legacy command that already returns multi-line `ERROR:` text** (e.g. `ListCommand.cs:24`
  includes examples) — `WrapLegacyText` maps it to `{"ok":false,"error":{...}}` with the full
  message as `message`; no code is invented beyond `INTERNAL` for unmigrated commands. Migrated
  commands return precise codes.
- **Serialization safety** — payloads use `Dictionary`/`List`; never `ArrayList` (ae1dda2). Reuse `MaxJsonLength = int.MaxValue`.
- **Null `Data`** — JSON success envelope falls back to `"text"` so the model always gets *something*.
- **`exec`** — arbitrary text output; in JSON mode returned as `{"ok":true,"text":"..."}` (no structured data). Acceptable.
- **Unicode / escaping** — `JavaScriptSerializer.Serialize` handles escaping; the response is UTF-8 (`SendResponse` already encodes UTF-8).

---

## 9. Verification (Revit running)

Build & restart via the script (never manual):
```powershell
.\build.ps1 -Debug
```
With a document open:

1. **Default text unchanged (regression guard).** Capture before/after:
   ```bash
   curl -s http://localhost:18884/info        > after.txt
   ```
   Diff against a pre-change capture — must be **byte-identical**. Repeat for `list walls`, `selected`.
2. **JSON via query param.**
   ```bash
   curl -s "http://localhost:18884/info?format=json"
   ```
   Expect a JSON object `{"ok":true,"data":{...}}` (migrated) or `{"ok":true,"text":"..."}` (unmigrated).
   Confirm `Content-Type: application/json`.
3. **JSON via Accept header.**
   ```bash
   curl -s -H "Accept: application/json" http://localhost:18884/info
   ```
   Same JSON as #2.
4. **Curl default stays text.**
   ```bash
   curl -s http://localhost:18884/info        # default Accept: */*
   ```
   Plain text, `Content-Type: text/plain`.
5. **Structured error — text mode (self-correction).**
   ```bash
   curl -s http://localhost:18884/list        # missing category
   ```
   Expect `ERROR: ...` followed by a `Hint:` line. Confirm it still `StartsWith("ERROR")`.
6. **Structured error — JSON mode.**
   ```bash
   curl -s "http://localhost:18884/list?format=json"
   ```
   Expect `{"ok":false,"error":{"code":"BAD_ARGS","message":"...","suggestion":"..."}}`.
7. **Unknown command + no document** — confirm `UNKNOWN_COMMAND` / `NO_DOCUMENT` codes in JSON, `ERROR:` in text.
8. **Batch JSON** (after Plan 02, or with the standalone batch path).
   ```bash
   curl -s -X POST "http://localhost:18884/batch?format=json" -d "info
   list walls"
   ```
   Expect `{"ok":true,"results":[...]}`; default (no `?format=json`) batch output unchanged.

Check `%LOCALAPPDATA%\VibeModel\logs\` for serialization or routing errors after each test.

---

## 10. Files to change

- **New:** `src/VibeModel/Services/Claude/CommandResult.cs` — `ResponseFormat` enum + `CommandResult` + `Render`.
- `IClaudeCommand.cs` — add `IStructuredCommand` interface (existing interface untouched).
- `ClaudeCommandRegistry.cs` — `_json` field; `ExecuteCore(...)` returning `CommandResult`;
  `Execute(..., ResponseFormat)` overload rendering it; `WrapLegacyText`; keep/scope the old
  `Execute(command,args,uiApp)` as a thin text wrapper for any remaining caller. (Plan 02's
  `IsModification` lives here too — no conflict.)
- `RevitCommandHandler.cs` — `CommandRequest.Format`; `EnqueueAndWait(cmd, args, fmt)`;
  pass `fmt` into `_registry.Execute` in `Execute` (`:103`). Batch path consumes `ExecuteCore`/`.Success`.
- `RevitHttpServer.cs` — capture `Accept` in `ReadHttpRequest`; detect format in `ProcessRequest`;
  thread `fmt` into `EnqueueAndWait`; add `ContentType` to `HttpResponse`; `SendResponse(contentType)`.
- Migrated query commands: `InfoCommand.cs`, `ListCommand.cs`, `SelectedCommand.cs`,
  `GetCommand.cs`, `ParamsCommand.cs`, `LevelsCommand.cs`, `ViewsCommand.cs` — implement `IStructuredCommand`.
- `CLAUDE.md` — document `?format=json` / `Accept: application/json`, the error envelope shape,
  and the error-code list (so the CLI Claude knows it can request JSON and can act on `code`/`suggestion`).
- *(Follow-up, separate change)* `AnthropicDirectBackend.cs:332` / `LocalLlmBackend.cs:397` —
  append `&format=json` to the loopback URL and parse fields. Out of scope for v1; the text-mode
  `Hint:` line already delivers self-correction to these backends.

---

## Summary of decisions

- **Contract:** Option (a) — a `CommandResult` type rendered as text *or* JSON at the HTTP
  boundary, with an **optional `IStructuredCommand`** so migration is per-command, not big-bang.
  Unmigrated commands are auto-wrapped; text mode is byte-identical to today.
- **Format detection:** `?format=json` or explicit `Accept: application/json`. Curl default
  (`*/*`) stays text. Threaded once: `RevitHttpServer` → `RevitCommandHandler` →
  `ClaudeCommandRegistry` → command.
- **Errors:** structured `{code, message, suggestion}`. Text keeps `ERROR: <message>` (+ additive
  `Hint:` line) so every `StartsWith("ERROR")` consumer — including Plan 02's batch — stays valid.
  Concrete code taxonomy in §6.
- **Highest-value first:** the text-mode `Hint:` line self-corrects all three backends with zero
  backend changes; structured JSON benefits ClaudeCodeBackend (CLI can request it) and external
  AI/curl users immediately; backend `&format=json` consumption is a deliberate follow-up.
- **Plan 02:** no conflict — batch decides on `CommandResult.Success` (or `StartsWith("ERROR")`
  in the interim), and `IsModification` is unaffected.
- **Serialization:** reuse `JavaScriptSerializer { MaxJsonLength = int.MaxValue }`; payloads are
  `Dictionary`/`List`, never `ArrayList` (ae1dda2).
