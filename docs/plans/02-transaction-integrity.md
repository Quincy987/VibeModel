# Plan 02 — Transaction Integrity (Undo Grouping + Batch Atomicity)

## Goal

Two related halves of one feature:

1. **Undo grouping** — a multi-step "vibe" should collapse to **one Ctrl+Z**, not N.
2. **Batch atomicity** — a mid-batch failure should leave a clean state and a single undo unit, never half-built orphaned geometry.

Both reduce to the same mechanism: wrap a set of per-command transactions in a Revit `TransactionGroup` and `Assimilate()` them into one undo entry — and the only place we can currently do that is `POST /batch`.

---

## 1. Current transaction model (with refs)

### One transaction per command
Every modification command calls a single helper:

`src/VibeModel/Services/Helpers/TransactionHelper.cs:16`
```csharp
public static string Execute(Document doc, string name, Action action)
{
    using (var trans = new Transaction(doc, name))
    {
        // WarningSwallower preprocessor, SetClearAfterRollback(true)
        trans.Start();
        try   { action(); trans.Commit(); return null; }
        catch (Exception ex) { if (trans.HasStarted()) trans.RollBack(); return "ERROR: " + ex.Message; }
    }
}
```

Key properties:
- **One `Transaction` per command**, auto-committed.
- A failing command **rolls back its own transaction** and returns an `"ERROR: ..."` string — it does **not** throw. So a single command never leaves half-built geometry *from itself*.
- The `"ERROR:"` prefix is the project-wide success/failure convention (used by every command and by `ClaudeCommandRegistry.Execute`).

Callers (all funnel through the helper):
- `WallCommand.cs:60`, `FloorCommand.cs:49`, `DeleteCommand.cs:47`, `SetCommand.cs:63`, `PlaceCommand.cs:72`, `ColorCommand.cs:43/61`, `ColorSplashCommand.cs:107/191`, `IsolateCommand.cs:58`, `UnisolateCommand.cs:22`.
- `HideCommand` / `ShowCommand` → `VisibilityHelper.cs:21/41` → same helper.
- `ExecCommand.cs:43` is the **one exception**: it opens its own `Transaction` inside dynamically-compiled code (functionally identical pattern).

Read-only commands (`info`, `list`, `selected`, `params`, `bbox`, `geometry`, `views`, `levels`, …) open **no transaction**.

### How requests reach the main thread
`RevitCommandHandler.Execute` (`RevitCommandHandler.cs:87`) drains a `ConcurrentQueue<CommandRequest>` one request at a time on Revit's main thread, calling `_registry.Execute(...)` per request. **No grouping today.**

### Why `/batch` is N undo steps today
`RevitHttpServer.HandleBatch` (`RevitHttpServer.cs:280`):
```csharp
foreach (var line in lines) {
    ...
    var result = _commandHandler.EnqueueAndWait(cmd, args);  // blocks per line
    sb.AppendLine(result);
}
```
Because the HTTP thread blocks on each `EnqueueAndWait` before enqueuing the next, **each command is a separate `ExternalEvent.Raise()` → separate `Execute()` drain cycle**. A `TransactionGroup` opened in one `Execute()` call would have to close before the next command — so grouping is impossible without restructuring batch into a single main-thread unit.

---

## 2. `TransactionGroup` API (confirmed semantics)

- `var tg = new TransactionGroup(doc, name); tg.Start();` opens a group. A group is **not itself a transaction** — it's a container.
- Individual `Transaction`s opened *inside* the group commit normally (read commands still need no transaction and run fine while a group is open).
- `tg.Assimilate()` merges **all committed child transactions into ONE undo entry**, labelled with the **group's name**.
- `tg.RollBack()` discards **every** child transaction's changes — even already-committed ones. This is what gives true all-or-nothing.
- Groups may nest (so `exec` opening its own transaction/group inside a batch group is fine).
- Must run on the main thread — guaranteed, since this all happens inside `RevitCommandHandler.Execute`.

---

## 3. Chosen approach

**Scope grouping to `POST /batch` for v1.** Restructure batch so the **entire** command list is handed to the main thread as **one request** and processed inside **one `Execute()` call**, wrapping the per-command loop in a `TransactionGroup`.

- Single (non-batch) commands stay exactly as-is — a single command is already one undo entry with a readable name ("VibeModel: Create Wall"), so no group is needed.
- The "one Ctrl+Z per vibe" goal (half #1) is achieved by **steering the AI to emit a multi-step vibe as a single `POST /batch`**. Documented in CLAUDE.md.
- Cross-turn grouping (a chat turn spanning multiple HTTP calls) is **out of scope for v1** — see §7.

Rejected alternative: hold a persistent `TransactionGroup` open across HTTP requests keyed by session. Rejected because an open group **locks the document** for the duration; an idle or abandoned turn would leave Revit un-editable and risk an orphaned group. Not worth it for v1.

---

## 4. Files / methods to change

### 4a. `RevitCommandHandler.cs` — add a batch request path
- Extend `CommandRequest` with an optional batch payload (null for single commands):
  ```csharp
  public IReadOnlyList<(string Command, string Args)> Batch { get; }
  public bool Atomic { get; }
  public bool IsBatch => Batch != null;
  ```
  Add a constructor overload taking the list + atomic flag.
- Add `EnqueueBatchAndWait(IReadOnlyList<(string,string)> commands, bool atomic)` — mirrors `EnqueueAndWait` but uses a **longer timeout** (see §9 edge case) and enqueues a batch request.
- In `Execute` (`:92` loop), branch:
  ```csharp
  request.Result = request.IsBatch
      ? ExecuteBatch(app, request)
      : _registry.Execute(request.Command, request.Args, app);
  ```
- New private `ExecuteBatch(UIApplication app, CommandRequest req)`:
  ```csharp
  var doc = app.ActiveUIDocument?.Document;
  if (doc == null) return "ERROR: No document open";

  var sb = new StringBuilder();
  bool anyModified = false, anyError = false;

  using (var tg = new TransactionGroup(doc, BuildGroupName(req.Batch)))
  {
      tg.Start();
      try
      {
          foreach (var (cmd, args) in req.Batch)
          {
              sb.AppendLine(">>> " + cmd + (string.IsNullOrEmpty(args) ? "" : " " + args));
              var result = _registry.Execute(cmd, args, app);
              sb.AppendLine(result);
              sb.AppendLine();

              if (result != null && result.StartsWith("ERROR")) anyError = true;
              else if (_registry.IsModification(cmd))           anyModified = true;
          }

          if (req.Atomic && anyError)        { tg.RollBack(); sb.AppendLine("[atomic] rolled back — a command failed."); }
          else if (anyModified)              { tg.Assimilate(); }
          else                               { tg.RollBack(); } // nothing to keep; avoid empty undo entry
      }
      catch
      {
          if (tg.HasStarted()) tg.RollBack();  // never leave a group open (would lock the doc)
          throw;
      }
  }
  return sb.ToString();
  ```

### 4b. `RevitHttpServer.cs` — parse batch + atomic, call new method
Replace the per-line `EnqueueAndWait` loop in `HandleBatch` (`:280`) with:
- Parse each non-empty line into `(cmd, args)` (same `Split(' ', 2)` logic as today, `:293`).
- Detect an opt-in atomic directive — a leading line `#atomic` in the body **or** an `?atomic=1` query param on the POST (see §6). Strip it from the command list.
- Call `_commandHandler.EnqueueBatchAndWait(list, atomic)` once; return its result.
- Keep the existing `">>> line / result"` output formatting (now produced inside `ExecuteBatch`).
- Bump `client.SendTimeout` for the batch response (`:125` is currently 35000) — see §9.

### 4c. `ClaudeCommandRegistry.cs` — expose modification check
Add:
```csharp
public bool IsModification(string command) =>
    _commands.TryGetValue(command, out var c) && c is IModificationCommand;
```
Used by `ExecuteBatch` to decide whether the group has anything worth assimilating (so a read-only batch produces no spurious undo entry).

### 4d. (Optional) `TransactionGroupHelper.cs` — encapsulate safety
A small helper mirroring `TransactionHelper` to centralize the `Start` / `Assimilate` / `RollBack` / try-finally-never-leave-open logic. Keeps `RevitCommandHandler` clean. Nice-to-have, not required.

### 4e. `CLAUDE.md` — document behavior
- Note that `POST /batch` now executes as **one undo unit** (one Ctrl+Z).
- Guidance: send multi-step vibes as a single `/batch` to get one-undo behavior.
- Document the opt-in atomic mode (`#atomic` first line or `?atomic=1`).

---

## 5. Where the Start / Assimilate / RollBack boundaries go

- **Single command (non-batch):** No group. Unchanged — one `Transaction` = one undo entry.
- **`/batch`, default (resilient):** `tg.Start()` before the loop → per-command transactions commit/rollback individually → `tg.Assimilate()` after the loop. One undo entry containing every command that succeeded.
- **`/batch`, atomic mode, a command failed:** `tg.RollBack()` instead of Assimilate → discards everything.
- **`/batch` with only read commands:** `tg.RollBack()` (nothing committed) → no undo entry created.
- **Unexpected exception escapes the loop:** `try/catch` → `tg.RollBack()` if still started, so the group is **never left open** (an open group locks the document).

---

## 6. Batch-failure semantics — decision + rationale

**Default: resilient (continue-on-error + Assimilate).** Run all commands; the group keeps every command that succeeded as one undo unit. A typo in command 5 does **not** destroy walls 1–4.

**Why this is safe (the key insight):** each command already self-protects via `TransactionHelper` — a failed command rolls back *its own* transaction and returns an `"ERROR:"` string. So a mid-batch failure **never leaves half-built geometry from the failing command**. The danger that remains isn't orphaned geometry — it's (a) N separate undo steps and (b) an inconsistent partial result. Resilient-assimilate fixes (a) outright and makes (b) a deliberate, recoverable choice (the user sees the per-command errors in the response and can fix-up or undo the whole unit).

**Opt-in: atomic (all-or-nothing).** For "build this whole room or nothing" intent, an atomic flag makes any command failure trigger `tg.RollBack()`, discarding the entire batch. Triggered by a leading `#atomic` line in the body or `?atomic=1` on the POST. Off by default because vibe modelling is exploratory — silently discarding 9 good commands because the 10th had a bad element ID is the more surprising/destructive default.

Trade-off summary: resilient = least surprising, preserves good work, one undo to discard if unwanted. Atomic = strict consistency, but one bad arg loses the batch. We make the resilient one default and expose atomic for when the user/AI explicitly wants it.

---

## 7. Cross-turn grouping — out of scope for v1 (and why)

A "chat turn" spans **multiple** HTTP requests, but the HTTP layer is stateless per request. Grouping across a turn would require either:
- Holding a `TransactionGroup` open across requests — **rejected**: an open group locks the document; an abandoned turn leaves Revit un-editable and the group orphaned.
- An explicit session protocol — `/group start` … commands … `/group commit` (or `/begin` … `/end`) backed by a persistent group **with an idle-timeout auto-rollback** to release the lock if the turn never ends.

**Decision:** ship `/batch` grouping now; defer session-scoped grouping. In practice, steering the AI to bundle a turn's mutations into one `POST /batch` captures ~90% of the benefit with none of the locking risk. The `/group start|commit` session protocol is the documented future path (a later plan) if per-turn grouping across separate curl calls is required.

---

## 8. Undo-entry naming

The `TransactionGroup` name becomes the undo dropdown label after `Assimilate()`. Build a readable summary from the command list, e.g.:
- `"VibeModel: Batch (4 commands)"`, or better, verb-counted: `"VibeModel: 4× wall"` / `"VibeModel: wall×3, floor×1"`.

`BuildGroupName(batch)` groups by command name and counts. Individual per-command transaction names ("VibeModel: Create Wall") are subsumed by the group and don't appear separately after assimilate. Single commands keep their existing specific names.

---

## 9. Edge cases

- **Batch timeout.** Restructuring makes a whole batch **one** request, so the existing 30s `CommandTimeout` (`RevitCommandHandler.cs:16`) now applies to the entire batch — a large batch could exceed it. Fix: `EnqueueBatchAndWait` uses a scaled timeout, e.g. `max(30s, 5s + 2s × N)` capped at ~5 min, and the HTTP `client.SendTimeout` (`RevitHttpServer.cs:125`) is bumped to match for batch responses. Without this, big batches falsely time out.
- **Group never left open.** Any unexpected throw inside `ExecuteBatch` must `RollBack()` the group. An open `TransactionGroup` locks the document — this is the most important safety invariant.
- **No document at batch time.** Return `"ERROR: No document open"` before opening the group (matches `ClaudeCommandRegistry` guard at `:32`).
- **Read-only / mixed batch.** Reads need no transaction and run fine inside an open group. If *no* command modified, `RollBack()` the empty group so Revit doesn't show a meaningless undo entry. Mixed batches assimilate only the writes.
- **`exec` inside a batch.** It opens its own transaction (and may open its own group) — nesting is allowed; it assimilates into the outer group normally. No change needed.
- **Queue-full / cancellation.** A batch is a single `CommandRequest`, so existing `MaxQueueSize` and `Cancel()`/timeout handling apply unchanged to the whole batch.
- **`"ERROR"` prefix detection.** Failure detection relies on the existing convention that failures return strings starting with `"ERROR"`. Consistent across the codebase; acceptable for v1. (A cleaner success/failure signal from `IClaudeCommand` is a possible later refactor but out of scope here.)
- **Atomic rollback of committed work.** Confirmed `TransactionGroup.RollBack()` discards already-committed child transactions — correct for atomic mode.

---

## 10. Verification approach (Revit running)

Build & restart via the project script (never manual):
```powershell
.\build.ps1 -Debug
```

Then, with a document open:

1. **Health**
   ```bash
   curl -s http://localhost:18884/health
   ```
2. **Default resilient batch → one Ctrl+Z**
   ```bash
   curl -s -X POST http://localhost:18884/batch -d "wall 0 0 5000 0
   wall 5000 0 5000 5000
   wall 5000 5000 0 5000
   wall 0 5000 0 0"
   ```
   - Expect 4 walls created. In Revit, press **Ctrl+Z once** → all 4 walls disappear together. Confirm the undo dropdown shows a single "VibeModel: 4× wall" entry, not four.
3. **Mid-batch failure (resilient default)** — include one bad command:
   ```bash
   curl -s -X POST http://localhost:18884/batch -d "wall 0 0 3000 0
   wall 0 0 0
   wall 0 0 3000 3000"
   ```
   - Expect 2 walls created, the middle line returns an `ERROR`, and **one Ctrl+Z** removes both good walls. No half-built geometry.
4. **Atomic mode**
   ```bash
   curl -s -X POST http://localhost:18884/batch -d "#atomic
   wall 0 0 3000 0
   wall 0 0 0"
   ```
   - Expect **zero** walls remain (the whole batch rolled back), response notes `[atomic] rolled back`.
5. **Read-only batch → no undo entry**
   ```bash
   curl -s -X POST http://localhost:18884/batch -d "info
   list walls"
   ```
   - Expect normal output, and **no** new entry in Revit's undo dropdown.
6. **Large batch timeout** — send ~30+ commands; confirm it completes without the 30s timeout error.
7. **Single command unchanged**
   ```bash
   curl -s "http://localhost:18884/wall?args=0 0 4000 0"
   ```
   - Still one undo entry named "VibeModel: Create Wall".

Check `%LOCALAPPDATA%\VibeModel\logs\` for any group/transaction errors after each test.

---

## Summary of decisions

- **Mechanism:** `TransactionGroup` + `Assimilate()`, scoped to `POST /batch` for v1.
- **Restructure:** batch becomes a single main-thread request (`EnqueueBatchAndWait` → `ExecuteBatch`) so one group can wrap the whole loop. Single commands untouched.
- **Default failure semantics:** resilient — keep successes, one undo unit (safe because each command already self-rolls-back). Opt-in `#atomic` / `?atomic=1` for all-or-nothing.
- **One-Ctrl+Z-per-vibe** delivered by steering the AI to send multi-step vibes as one `/batch`; cross-turn grouping deferred (locking risk) with `/group start|commit` as the documented future path.
- **Safety invariants:** never leave a group open (try/finally rollback); scale the batch timeout; roll back empty (read-only) groups.
- **Files:** `RevitCommandHandler.cs` (batch path), `RevitHttpServer.cs` (parse + call), `ClaudeCommandRegistry.cs` (`IsModification`), optional `TransactionGroupHelper.cs`, plus `CLAUDE.md` docs.
