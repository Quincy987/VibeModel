# Plan 15 — Approve before keeping, preview, read-only runs

> Goal: what pyRevit 7.0's agent runtime (2026-10-06) does that VibeModel did not — in VibeModel's
> own shape. A modeller can have Revit show a deletion or edit **before** it is kept (Keep /
> Discard, one undo entry); the AI can **preview** a change without touching the model; a
> **read-only** policy (or a read-only `exec`) can never change the model. Off by default: the user
> asked for approval to be opt-in (2026-10-19).

> **STATUS (built and verified live 2026-10-19, branch claude-bot/t-352).** Headless tests
> (`ChangeApprovalTests`); live in Revit 2023 on a copy of the annotation-verification model —
> results below.

**Prior attempts:** plan 02 §7 rejected holding a `TransactionGroup` open ACROSS HTTP requests (an
open group locks the document). This plan never does: the command, its change set, the window and
the decision all happen inside ONE `RevitCommandHandler.Execute` call — like pyRevit, whose prompt
also blocks the run.

## Decisions

- **Policy** `ChangeApproval`: `auto` (default — no group, no new lines, byte-identical answers),
  `ask`, `readonly`. Sources: managed.json → `VIBEMODEL_APPROVAL` → settings.json → auto; an
  unreadable value is skipped. "Deze Revit-sessie niet meer vragen" turns ask into auto until Revit
  closes (Settings shows it and can clear it).
- **Guarded** = the annotation guard's set (`delete`, `exec`, `set`, `join`, `unjoin`, `phase`) and a
  batch holding one: the commands that delete or edit existing elements. Creating never asks.
- **One run group** per command/batch serves the annotation guard, approval, preview and read-only.
  Opened only when `ChangeApprovalPolicy.NeedsRunGroup` says so (a modifying command and: a flag, or
  readonly, or ask + guarded). After the command, one pure decision (`Decide`): Keep / Prompt /
  Preview / ReadOnly / QueryModified / Nothing. Success does not matter: an exec that failed
  half-way may have committed transactions, and the user should see those too.
- **Prompt**: isolate + select the added/modified elements the active view can show, inside a
  NESTED group that is rolled back on both answers (the view returns exactly to what it was — a
  temporary isolate the user had included); then the modal WPF window, owned by Revit's main window
  (Revit is restored first when minimized — an owned window is hidden with a minimized owner).
  Anything failing in that path is a Discard.
- **Change set** with categories: added/modified read live; deleted from categories pre-read for the
  named ids and their `GetDependentElements` (≤ 5000). Hidden: elements without a category (not
  types), sketch lines, automatic sketch dimensions. An unreadable deletion counts as "other", last —
  an exec deletion must never read as "delete 0". Views and types are not isolated or zoomed.
- **exec inside a group**: under ask/preview/readonly exec runs inside the run group, so Revit
  refuses to save, close or synchronize there; exec adds a hint. Under auto exec is untouched.
- **Waiting**: guarded requests under ask wait 10 min (HTTP, `RevitWebClient` in the pane, socket);
  then `APPROVAL_PENDING`. A decision the client never received is shown once in the next stamp. The
  rules block tells the model under ask to give its shell 600000 ms for such calls and never to
  retry a discard; under readonly that changes are refused.
- **Bookkeeping** after a rolled-back group: the edit counter is put back (inner commits were counted
  as they happened) and the ids leave the "created by VibeModel this session" set. Discards are
  journaled, previews/refusals are not.
- Out of scope (later plans): before/after parameter values in the window; `?preview=1` for the API
  backends' tools (they pass only args); an MCP server; the theme.

## Verification (Revit 2023, 2026-10-19)

Model: `demo_out/t352/verify.rvt` (copy of `annot-verify.rvt`: 62 walls, 14 floors, 574 piles, 21
spot elevations on floors in sections). Revit started detached via WMI with `VIBEMODEL_DEBUG=1`;
the policy switched per process (`Environment.SetEnvironmentVariable` through exec, later
`VIBEMODEL_APPROVAL=ask` at launch); windows answered with UI Automation by AutomationId.

- **auto**: `wall`, `delete` of that wall, an exec read — normal answers, no window, no new lines.
- **ask, delete a pile**: window after 0.34 s. Discard → `ERROR: Discarded by the user.` + hint,
  pile still there, edit counter 4 → 4. Keep → `DELETED 1 ELEMENT(S)` + `-- approval: kept by the
  user (#3)`, counter 4 → 5; ONE Ctrl+Z (PostCommand Undo) brought the pile back.
- **ask, delete a floor carrying a spot**: window shows "Verwijderd 2 (Floors 1, Spot Elevations 1)"
  and the `-- annotations: removed 1 spot elevation …` line (before the dependents pre-read it read
  "Verwijderd 65 (overig 64, Floors 1)" — the sketch). Discard → floor and spot back; Keep → both
  gone; one Ctrl+Z → both back.
- **ask, exec**: a read → no window. Moving a free wall 1000 mm → "Gewijzigd 1 (Walls 1)",
  "Geïsoleerd en geselecteerd in deze view: 1"; Keep → wall at x=1000, temporary isolate OFF,
  selection back to 0; one Ctrl+Z → x=0. Moving a joined wall: Revit refused ("Can't keep elements
  joined"), empty change set → no window, Revit's error returned.
- **The user's own temporary isolate** survived a prompt (still active afterwards).
- **exec + `trans.Commit(); doc.Save()`** inside a run group: "Operation is not permitted when there
  is any open transaction phase started by API client" + the new hint (Keep) or inside the DISCARDED
  message (Discard); the document stayed editable. Under auto the same snippet saved.
- **Batch** `delete` + `wall`: one window (added 1, modified 4, deleted 1, isolated 3); Discard →
  `[approval] rolled back — Discarded by the user. (#6)`, counter unchanged.
- **Closing with X** and **Esc** = Discard. **JSON**: `{"ok":false,"error":{"code":"DISCARDED",…},
  "meta":{…},"approval":{"id":9,"decision":"discarded","by":"user"}}`.
- **"Niet meer vragen"** ticked + Keep → the next delete kept without a window; Diagnose:
  `Change approval: ask (VIBEMODEL_APPROVAL) - not asking for the rest of this Revit session`.
- **Minimized Revit**: the window was found after 0.63 s and Revit was restored.
- **Preview**: `wall …&preview=1` → `-- preview: nothing was changed. Would add 1 (Walls 1), modify 0,
  delete 0. Ids are provisional.`, counter unchanged; a batch preview under ask → no window, nothing
  kept; JSON `preview` object with ids and categories.
- **`?readonly=1`** exec moving a wall → `QUERY_MODIFIED_MODEL` (modified 1), wall unmoved; a read with
  `readonly=1` → normal.
- **readonly policy**: `wall` and `delete` → `READ_ONLY` + preview line; an exec read → normal; a batch
  → `[read-only] rolled back — …`.
- **Pending**: the window left open for 615 s → at 600 s the client got `ERROR: still waiting for the
  user's decision in Revit.` + `Hint: call 'approvals' …`; Discard at 615 s → the next command's stamp
  `-- model #1 | view: __WCS_N.A.P. | selected: 0 | approval #2: discarded`, the one after without it.
- **Not verified live**: a chat-pane turn. On this PC the Claude Code subscription is disabled by
  the organisation and there is no API key (see `docs/verify/server-lockdown-2026-10-16.md`), so no
  model call can run. Covered instead: the rules/turn-prompt text per policy and the in-pane
  `RevitWebClient` raising its timeouts to 605 s (unit tests).
- **Found and fixed live**: the floor's sketch in the counts (dependents pre-read), the view counted
  as something to show, `doc.Save()`'s bare message, a discarded failing command losing its own
  error, and the exec compile hint (plan 16) never firing on a Dutch compiler.
- `approvals` lists every decision; discarded runs are in the project journal
  (`· delete 1602467 · discarded by the user`).
