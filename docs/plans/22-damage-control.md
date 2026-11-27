# Plan 22 — damage control (phase 2)

Follows v1.2.22 (phase 0 + 1). Source: a colleague's 45-chat export (kept out of git). One `exec`
replaced 10 filled regions in sections and the 256 dimensions on their edges went with them; its
"dry run" was a switch inside the script (`bool CREATE = false`) that skipped exactly the delete;
the assistant then improvised `uiApp.PostCommand(PostableCommand.Undo)`. The export stored only the
commands and ✓/✗, so what VibeModel answered at that step was lost.

Decisions (the user, final): **no approval window in `auto`**, not even for large losses — one
overview per action (never per element) plus an own undo. A dimension whose edge is gone with
nothing in its place is rightly removed (and said so); only a new edge in the same place brings it
back. A changed sheet, a detached view template and a changed filter are named, not asked about.

## 1. Every modifying exec is one undo entry, also under `auto`

`ChangeApprovalPolicy.NeedsRunGroup(…, command, args)`: a modifying `exec` always runs in a run
group (`ExecRunsInGroup`), unless its source saves, closes or synchronizes (`SavesOrCloses` — Revit
refuses those inside a group; such a snippet runs exactly as before). Under `auto` the group's
decision is `Keep` (no prompt, no `-- approval:` line, no ApprovalLog entry); an exec that changed
nothing rolls its empty group back (no empty undo entry). The annotation guard's re-host is in the
same group, so it is one entry with the snippet.

Measured (Revit 2023, demo model, 2026-11-27):
- a snippet creating a level in the wrapper transaction, then `trans.Commit();` and a level in its
  own `Transaction` → `-- changed: added 2 (Levels 2)`; ONE posted Undo removed both levels;
- `doc.EditFamily(fam)` + a change in the family + `fd.LoadFamily(doc, new KeepValuesLoadOptions())`
  inside the group: works (no refusal), and one Undo took it back — no extra exemption needed;
- cost: exec ×10 creating a level, median 506 ms without the group (old path) and 502 ms with it —
  the compile dominates; no difference measurable.

A consequence: an exec that changes something the office protects (`ForbiddenActions`) is now
rolled back under auto too (FORBIDDEN), where it used to stay with a `-- forbidden: … press Ctrl+Z` line.
Not measured: a snippet that opens and edits another project document inside the group.

## 2. The `undo` command

`UndoLedger` (pure): per document the kept runs, newest last — undo name (the group name, else the
last transaction the run committed), summary (command + `-- changed:`), the edit counter before and
after (`ModelChangeTracker.GetTotalEdits`), whether it is one undo entry for sure (a VibeModel group,
exactly one transaction, or `extrude/sweep/lwall`, which group their own work). `undo` takes
back the LAST run by posting Revit's Undo (`PostableCommand.Undo`) and refuses — naming why — when
there is none, when the counter moved since (the user, Revit's own Undo/Redo, another client: then
the top of Revit's list is not that run), when the run may be several entries, inside a batch, when
another document holds VibeModel's last change, while an earlier undo is on its way, or when Revit
cannot post. The posted command runs after the request; DocumentChanged confirms it
(`ObserveUndoRedo`: the undo is the first change after the post — one Revit Undo takes back exactly
the top entry, which the counter check proved was this run) and the next stamp says
`| undone: VibeModel: exec …` (JSON `meta.undone`), `the posted undo took back '…', not '…'` (the
model changed in between) or `undo of '…' did not happen` (a redo, or not seen within 20 s). That
undo is not counted as a USER edit. After it, the run below becomes the top (its counter set to where
the model stands). When the user's own Ctrl+Z right after VibeModel's last run takes it back (the
counter moved by one and the undone names include the run's), it leaves the ledger too; the run below
only becomes undoable when every undone transaction was that run's (an undo of several steps at once
may have taken more).
Opening a document or synchronizing it with central empties its ledger (Revit's undo list starts
afresh). `undo --status` lists the runs. A preview turn reports what it would take back. Journaled
(`· undo · took back 'VibeModel: exec …'`); refused on the file-based fallback; exec refuses a
snippet that posts `PostableCommand.Undo/Redo` (`ExecSourceChecks.PostsUndo`, code `EXEC_POSTS_UNDO`).

Measured: undo after a kept exec, after a batch (both levels gone) and after an `ask` Keep
(the deleted level back); two undos in a row (the second ran after Revit had run the first);
refused after Revit's Undo button was used (`1 edit since …`), and after the user undid VibeModel's
batch with that button, `undo` offered the run before it. In a sketch (floor edit mode) Revit runs no
ExternalEvent: the request times out after 30 s and nothing is undone (the request is dropped);
cancelling the sketch commits an edit, so `undo` then refuses.

## 3. One overview per action

`-- attention:` (`AttentionReport` pure, `AttentionGuard` Revit side): sheets (number, name), views
(view template, filters) and filter elements read before a modifying run and compared by value for
the ids that existed before — `sheet 0002 renamed 'A' → 'B'; filter 'Doorsnede type' added to 1 view
('P2 regions')`; a template taken off or swapped, a filter removed, deleted or changed (Revit marks
it modified). After `-- changed:`; in a preview `-- preview: attention: …`; never after a rollback;
JSON additive `attention:[{kind,text}]` (in `preview` for a preview). The document is read again
only when the run modified or deleted a sheet, view or filter of the snapshot. At most 3000 views are
read; the rest only by id, and a changed one among them is named (`N changed views not checked …`). Measured: no `> 50 ms` log line on the demo model
(86 views, 3 filters, 2 sheets). Detaching a view template through exec is refused anyway
(FORBIDDEN, the template rule), so that item shows up through other routes.

`-- annotations:` now says why per group: `removed 256 dimensions that referenced … (view …) — reference
gone, nothing came back in its place` or `— could not be re-attached to the replacement` (Revit refused
the new one, or its view is gone); JSON `removed[].reason` (`nothingInPlace` / `couldNotAttach`).
Still one line per action, ≤ 10 views named. Revit's automatic sketch dimensions (`OST_WeakDims`, a
filled region's own sketch) are no longer recorded — measured: they were reported as "removed".

Rules: `SideEffectsRule` (pass on `-- annotations:` / `-- attention:` per action, deleted versus
created counts, offer `undo`), `ExecFailuresRule` (no silent catch per item), `UndoCommandRule`.

## 4. Dimensions on filled-region edges

A dimension reference on a filled region's straight edge is recorded with the edge's end points.
On reconcile a lost one is replaced by a straight edge of a filled region the run added in the same
view (or of the same, changed region) that lies on the old reference line (±1 mm along the
dimension), runs the same way and overlaps the old edge (gap ≤ 50 mm) — `AnnotationMatch.SameEdge`,
pure; the closest wins. Nothing there → removed, reason `nothingInPlace`.

Measured: a region's view geometry is a Solid whose edges carry references that `NewDimension`
accepts. Replaced by the same shape → both dimensions back; by a lower shape (top edge moved) → the
width dimension back, the height dimension removed with the reason; region deleted → removed with the
reason.

## 5. A trial is a preview of the real script

`DryRunRule` in the rules; `ExecSourceChecks.DryRunSwitch` adds `Hint: a switch inside the script
(CREATE = false) hides what the real run does …` to an exec that declares one (not in a preview).

## 6. What VibeModel answered, stored per step

`ToolCallDescriber.StatusLines`: the ERROR line(s) with their Hint, every `-- ` line and a batch's
`[…] rolled back` line (JSON: error, each failed batch command, the status keys compacted, `meta`), scrubbed,
≤ 1200 chars; only for VibeModel commands, never the CLI's own tools (`@read`, `@shell`). On
the ToolFinished event → the step → `ChatSessionStep.Status` (`status`, additive). The export prints
`Wat VibeModel antwoordde bij stap N (cmd):` + a `text` block; the scorecard reads it
(`stepsWithStatus`, `stepsRemovingAnnotations`, `annotationsRemoved`, `attentionLines`; the phase-1
baseline numbers are unchanged, counting version stays 1).

## Not in scope

An approval window in `auto`; safe copy/mirror (phase 3); the read-only "modified 1" false positive;
the guard's 5000-annotation cap; server-side per-step recording; `region --add-only`.
