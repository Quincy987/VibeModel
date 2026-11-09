# v1.2.16 live verification (annotation-only, saved exec scripts, forbidden actions)

Revit 2023, 2026-11-09, a throwaway copy of the quay-wall demo model (never saved), a separate Revit
instance, the branch build. HTTP with the session token; the pane through UI Automation invoke only.

**Alleen opmaak, HTTP and pane.** Chip *Alleen opmaak*: `approvals` = `annotate (this session)`. Kept, each
with its `-- changed:` line: `text`, `section`, `view 3d`, `region`, `sheet`, `color`, `hide`, `show`,
`viewport`, `dimension` between two grids, an `exec` that adds a text note. Refused and rolled back:
`wall` (`added 1 (Walls 1)`), `set` on a wall, `delete` of a floor, an `exec` moving a wall (`modified 2
(Floors 1, Walls 1)`), an `exec` deleting a wall (`deleted 2 (unread, treated as model 2)`), a batch holding
a wall next to a text note. JSON: `ANNOTATION_ONLY` + `preview`. The wall and the floor were still there and the
wall had not moved. `?preview=1` still previews. Settings has the radio; `managed.json` with `ChangeApproval:
annotate` locks the chip, tooltip *beheerd door Revit-beheer*. A new section or 3D view reads `added 1
(Views 1)` (first measured as 6 and 7: its camera, section box, sun settings; these are now left out).
`tag` was not tested live: the model has no wall tag family.

**Recipes.** A kept `exec` is saved as `VibeModel\recipes\<date> <question>.cs` with the header and the
script verbatim; the journal line says `· recipe <file>`; `recipes` lists, `recipes <n>` prints, an unknown
name is an error. No file after a preview or refusal. A snippet of one `//` line is refused. A failed `exec`
that kept earlier work is saved and journaled `· failed; changes kept`. Each `exec` of a batch is saved.

**Forbidden actions.** Refused before the run (`FORBIDDEN`): moving the base point, `set` Pinned on a
pinned datum, an `exec` unpinning one. Reading them passes; pinning is allowed; a new pinned grid gives no line.
Past the scan: an unpin and a view template's detail level under *Automatisch* start the response with
`-- forbidden: … changed; press Ctrl+Z to undo` (WARN logged, journaled `· forbidden …`); the same template
edit under *Vragen* is rolled back, `Refused: this change touched a view template`, the detail level unchanged.
Capturing the protected items never reached the 50 ms log threshold on this model.

**Real chat turns, 8/8 (Claude Code CLI).** Under *Alleen opmaak*: "Maak een wand van (0, 80000) naar
(4000, 80000), 3000 mm hoog." answers that VibeModel is on *Alleen opmaak*, that the change was rolled back,
and to set the chip to *Automatisch* or *Vragen*; no wall was added. Under *Automatisch*: a request for an
`exec` script writes both Comments, and the saved script carries `// Doel:` and `// Aannames:`; the file name
comes from the question, and `recipes` shows the `Doel:` line.

**Known limit.** `delete selected` and `set selected Pinned 0` name no id, so the check before the run does
not see them; the comparison afterwards does, as for any change past the scan.

**Test harness note.** Twice a script's focus-free invoke of the chip did not produce the menu rows within 15 s
(Revit in the background; the cause was not established). A retry a few minutes later opened the menu at once.

Tests: 1708 passed (Revit 2023) in five of six runs; one run had one failure that was not captured and did not come back in the next four, also after the same build sequence. Log: no ERROR, only the WARN lines for the forbidden changes above.
