# Plan 21 — Annotation layout: read it back, keep it apart

> Goal: a sheet the chat annotates (tags, dimensions, spot elevations, text) must be readable.
> Today the chat can SAY things overlap (it sees a screenshot) but cannot measure where they are,
> and the commands put every annotation on the same fixed default spot.
> Source: interview Ruben Wolbers, 12 Nov 2026, problem 1 (`demo_out/successcases/interviews/…`,
> gitignored) — "maatvoering en tags over elkaar, teksten scheef, hij meldt dat het overlapt maar
> kan het niet oplossen".
> Prior attempts: none. `sheetinfo` grades viewport/schedule boxes only (`SheetLayout`, plan 12);
> nothing ever read back an annotation's position.

## Step 0 — measured (Revit 2023.1, 12 Nov 2026)

Test model: a copy of the annotation-verify model (`demo_out/t496/annolayout.rvt`, gitignored);
three `hout_200` walls 800 mm apart on a 300 mm floor, section **AL test** at 1:20 across them.
Numbers are **paper mm** in the view frame (x along `RightDirection`, y along `UpDirection`,
origin `view.Origin`). Before picture: `.verify/annotation-layout/before.png` (gitignored).

### Reproduced mess (the commands as they were)

| What | Measured | Why |
|---|---|---|
| 3 spot elevations 100 mm apart (5 paper mm) on wall tops | text centres 42.5 / 45 / 47.5, all at y 212.1 → the three "+2.500" print over each other | every spot gets the same leader: shoulder 12 mm up, text 12 mm right (`AnnotationGeometry.DefaultLeader`) |
| `dimension 1730920 1730921 1730922` and `dimension 1730920 1730922` | both lines at y 208.0, bounding boxes identical (x 34.5..125.5, y 201.6..213.7); "1400" prints over "200"/"600" | the line always lands 8 paper mm past the elements (`PaperOffsetMm`) |
| spot shoulders vs dimension text | spot box y 200..216.8 crosses the dimension text band y 209..213.7 | same 8 mm / 12 mm defaults stack on the same band |
| two `text` notes 150 mm apart | boxes 43..70.5 × 123.5..137 and 50.5..81.9 × 125.7..134.5 overlap 20 × 8.8 mm | nothing checks |

### API: what can be set (all available in 2023, `apilookup` + live)

| Member | Settable | Note |
|---|---|---|
| `IndependentTag.TagHeadPosition` | yes | already used by `tag --at` |
| `IndependentTag.HasLeader`, `LeaderEndCondition` (Attached/Free) | yes | |
| `IndependentTag.GetLeaderElbow/SetLeaderElbow(Reference, XYZ)`, `HasLeaderElbow`, `GetLeaderEnd/SetLeaderEnd` | yes (per tagged reference; the 2022+ multi-reference API — the old `LeaderElbow` property is gone) | live read-back still to measure (see below) |
| `IndependentTag.TagOrientation` (Horizontal/Vertical/AnyModelDirection), `RotationAngle` | yes | `AnyModelDirection` follows the element → skewed tags |
| `IndependentTag.TagText` | read | the text the head shows — used for the size estimate |
| `Dimension.TextPosition` / `DimensionSegment.TextPosition` | yes; read back = **centre of the text, 1 paper mm above the line** (segment '600' origin (60,208), text (60,209)) | `IsTextPositionAdjustable()` true for every segment measured; `ResetTextPosition()` |
| `Dimension.Curve` | read-only | a dimension line is placed at creation (or moved with `ElementTransformUtils.MoveElement`) |
| `SpotDimension.TextPosition`, `LeaderEndPosition`, `LeaderShoulderPosition`, `LeaderHasShoulder` | yes; text centre 5 mm right of the shoulder, 0.1 mm above the leader end | the bend/end are also given at creation (`NewSpotElevation`) |
| `TextNote.Coord`, `Width`, `HorizontalAlignment`, `VerticalAlignment`, `KeepRotatedTextReadable`, leaders (`AddLeader`, `Leader.Elbow/End`) | yes | `Width` is the wrap width; `Height` read-only |
| `MultiReferenceAnnotation.Create(doc, viewId, options)` | exists; the office template has 3 Multi-Rebar Annotation types (`IB_Tag_Rebar_*`) | follow-up, not built here |

**API limitations (real):** Revit reports no box for a tag head or a dimension's text; the
bounding box of a dimension covers the whole string plus its witness lines (91 × 12.1 mm for an
80 mm string), a spot's covers origin → leader end (12 × 16.8 mm), not the text. There is no
"arrange annotations" call in the API (nor in the UI). Everything else above is VibeModel's to do.

### Extents — bounding box or estimate

- **Text note:** the bounding box is good: `Width`/`Height` (23.5 × 9.5) plus ~2 mm border
  each side (27.5 × 13.5). Use the box — except for a note with leaders (the box may take the
  leader in): there `Coord` (= the top-left corner for left/top alignment, measured (45,135) for
  a box from x 45, y 135 down) with `Width`/`Height` by alignment, + 1 mm.
- **Dimension text:** estimate per segment — centre `TextPosition`, width = characters × text
  size × width factor × 0.72 (NLCS-ISO: "Wapening K12-150" 16 chars at 2.5 mm = 27.4 mm → 0.685;
  "Wand hout 200" 13 → 23.5 → 0.72), height from `TextPosition.y` up 1.9 × text size (box top
  213.7 − 209 = 4.7 mm at 2.5 mm). The line itself: a thin strip between the outer references.
- **Spot text:** same estimate around `TextPosition` (value printed at 3 decimals in m:
  `+2.500` = 6 chars), plus the leader (origin → shoulder → end) as thin strips.
- **Tag head** (measured in verify, IB rebar tags at 1:20): `TagHeadPosition` lands exactly where it
  is set; the text stands ON it, mid-width, with the family's underline at the head. Without a
  leader the bounding box is the label frame (36 × 16.5 mm for "1x ø6", centred on the head) — its
  width is ink (the underline), its height is not; with a leader the box takes the leader in
  (y 72.5..103.2). Box used: the frame's width (no leader) or the `TagText` estimate, from the head
  up 1.9 × 2.5 mm. `SetLeaderElbow`, a free `SetLeaderEnd`, `TagOrientation.AnyModelDirection` +
  `RotationAngle` (radians) all read back as set; an attached leader's end cannot be read
  ("no leader end because the tag does not use a free end leader").
- **Rebar:** `IndependentTag.Create` refuses `new Reference(rebar)` and the bar's centre-line
  curve reference ("The reference can not be tagged"); the sub-element reference
  (`rebar.GetSubelements()[0].GetReference()`, `…:2000000:SUBELEMENT`) works — `tag` and the
  annotation guard use it (`AnnotationGeometry.TagReference`).
- **Witness lines** run from each reference to the line (covered by the dimension's box); on a
  200 mm wall at 1:20 they cross a spot's text on that wall whatever the slot, so a witness line
  through a text is `INFO`, and slots do not treat them as taken.

### Found on the way

`spot <x> <y>` failed in every section on a wall ("Error occurred when creating Spot Dimension!"):
the snap took the wall's **cut face** (the hatch surface in the view plane, stable reference
`…:-9999:SURFACE`), which a spot cannot use. A planar face whose normal runs along the view
direction and that lies within 1 mm of the cut plane is now skipped; `spot <id> top|bottom` was
not affected.

## Design

### a. `annolayout [view]` + the `-- layout:` line
- Pure: `Services/Claude/AnnotationLayout.cs` — `AnnoBox` (id, kind, paper rect, rotated,
  host), `TextExtent`, `Overlaps` (graded: ≤ 1 mm strip `INFO`, more `WARN`; a dimension line
  through a text `WARN`; two parallel dimension lines < 2 mm apart `WARN` "stacked"), wording.
- Revit: `Services/Claude/AnnotationExtents.cs` collects the boxes of one view (cap 2000).
- Command `annolayout [view name|id|sheet]` (read-only): boxes, CHECKS, `Verdict:`; JSON
  `{view, annotations, overlaps, rotated}`.
- After every kept modifying command / batch that ADDED annotations (also re-hosts — they are
  new elements): one line `-- layout: 2 overlaps: spot 1730946 ↔ spot 1730947 (5 × 4 mm); … →
  run 'annolayout <view>'`, only the new ones against their neighbours; JSON `layout`. Never after
  a preview or a rollback.

### b. Avoid by default
- `dimension`: rows. The new line goes to the first free row beyond the elements (8, 16, 24 … mm
  on paper); a row is taken by a parallel dimension within 4 mm whose span overlaps. `--offset`
  still places exactly. The response says `Placed: row 2 …`.
- `spot`: without `--leader` the text takes the first free slot (up-right, up-left, 24 mm
  up-right, 24 mm up-left, 36 mm; mirrored for bottom), the response names it. The left slots
  mirror the measured right-hand text position (centre 5 mm past the shoulder) — to confirm live.
- `tag`: rebar and multi-category fallback, `--type`, `--elbow x y`, and a free head slot with
  `--leader` without `--at` (the same slot choice as the spot, `AnnotationLayout.PickSlot`; the
  head is taken as the label's centre — to confirm live).
- `text --width <paper mm>`: wraps long notes.

### c. Prompt
`ChatBackendBase.AnnotationLayoutRule` — annotate with the commands, read `annolayout`, fix every
WARN before reporting, keep text horizontal, copy a hand-corrected example's spacing.

### Not in this plan
Rearranging existing annotations (would undo the user's hand corrections); Multi-Rebar
Annotation (`MultiReferenceAnnotation.Create`, needs a configured type).

## Risk
Read-only check and one prompt constant: low. Defaults that move (a dimension row, a spot slot)
only change when a neighbour is there. Estimates can be off for other fonts: thin overlaps are
graded `INFO`.

## Verify (live, Revit 2023.1, 12 Nov 2026 16:20–16:27, build 03ae979)

Fresh copy of the test model, section **AL na** at 1:20, the same three walls and floor as the
before picture (`.verify/annotation-layout/before.png` → `after.png`, gitignored):

| Step | Result |
|---|---|
| `spot 300000 2500` (and the two other wall tops) | placed on the wall's top face — the cut-face fix (before: every `spot x y` on a wall failed) |
| `spot 300250 0`, `spot 300450 0` (10 mm on paper) | second: `Leader: text up-right 24 mm (up-right taken by spot …)`, no `-- layout:` line |
| `dimension w1 w2 w3`, `dimension w1 w3` | `Placed: row 3 (24 mm …; row 1/2 taken by spot … text)`, then row 4 (row 3 taken by the first string) — no longer on one line |
| two overlapping `text` notes | `-- layout: 1 overlap in 'AL na': text … ↔ text … (20×8.8 mm)`; `annolayout` WARN + verdict |
| fix: `delete` + `text … --width 25` at a crowded spot | caught again (`text ↔ spot`, `spot leader through text`); on a free spot: `annolayout` → `Verdict: readable` |
| `tag <wall> --leader` (no wall tag loaded) | multi-category tag, `Head: free spot right above the element` |
| rebar tag | `new Reference(rebar)` refused → fixed through the sub-element reference (measured with exec; the `tag` run with that fix was overwritten, see below) |

Not run live with the last build (2508657 — rebar sub-element reference, witness lines, tag text
on its head): another session deployed its own build into the shared add-ins folder one second
after this one (16:28:33) and kept using Revit. Those three are covered by unit tests and the exec
measurements above; a live `tag <rebar> --leader --type …` and a look at witness INFO lines remain.
