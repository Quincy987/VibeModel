# v1.2.15 live verification (change line, truncation, journal user, mode chip)

Revit 2023, 2026-11-09, a throwaway copy of the quay-wall demo model (never saved), a separate Revit
instance, the branch build. HTTP with the session token; the pane through UI Automation invoke only.

**HTTP, 26/26.** `wall` under auto: `-- changed: added 1 (Walls 1), modified 0, deleted 0`, before the
stamp; JSON `changes` with count, category and ids. `delete` of a floor: `deleted 1 (Floors 1)`, its
sketch not counted. A batch: one line, one object. Nothing after a preview, a rolled-back atomic batch,
a failed `wall`, an `extrude` that failed after loading its family (group rolled back), a read-only
`exec`, or an `exec` whose only transaction rolled back. A failed `exec` that committed first keeps its
line after the error. `extrude`: `added 2 (Generic Models 1, Types 1)`. `list structural foundations`:
`showing 100 of 574 — incomplete; …`, JSON total/shown/truncated. `filter foundations`:
`showing 50 of 574 — incomplete; …`. `filter foundations --count Type`: type names.
`filter foundations Type contains …` and `"Type Name" contains …`: found. `colorsplash walls Type`:
names. `journal today`: `· by <user> ·` after the timestamp.

**Pane and approval, 21/21.** Chip *Automatisch* → *Vragen*: `approvals` = `ask (this session)`; a `delete`
opens the approval window. Keep: `-- changed: … deleted 1 (Walls 1)`, then `-- approval: kept`. Discard:
`ERROR: Discarded by the user.`, no changed line, journal `· by <user> · delete … · discarded by the user`.
Settings shows the chip's choice as a ticked box; unticked + Save clears it, chip back to *Automatisch*.
*Alleen lezen*: `wall` refused, no changed line. A legend on a second sheet is placed; the same legend
again on that sheet: `a legend can be on several sheets, but only once per sheet`. `managed.json`
(user-level) with `ChangeApproval: ask`: `ask (managed.json)`, chip *Vragen*, tooltip `… beheerd door IT`, no
menu; file removed: back to auto.

**Real chat turns, 9/9 (Claude Code CLI).** "Maak een wand van (0, 70000) naar (4000, 70000), 3000 mm
hoog.": the answer closes with *Toegevoegd: 1 wand (Walls 1), gewijzigd 0, verwijderd 0; aannames: type
`Beton_300` (eerste wandtype), level N.A.P., fase `2. Bouwfase`; overgeslagen: BIM-ID invullen.* The journal
has the wall with user and chat question. *Volgend bericht als preview* + "Verwijder wand …": badge on the
chip and gone after Send, the wall still exists, the answer says nothing was changed and what would be.

**Found by this run and fixed:** a family load counted a preview legend component and its type under the
family's category (`added 3 (Generic Models 2, Legend Components 1)`); `filter "Type Name"` read an empty
instance parameter instead of the type's.

Not run live: the coloured-dot fallback without Segoe MDL2 Assets; Revit 2022 and 2024 (compile only).
