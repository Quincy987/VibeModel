# Plan 12 — Compare: drawing and specification against the model

> Goal: close the two gaps in README "What it can do" §7 — "automatic drawing-vs-model
> comparison ❌" and "checking against a specification 🟡 (a signal, not proof)" — with one
> command family, `compare dwg | pdf | spec`, that turns "does the model match the drawing /
> the bestek?" from a chat improvisation into a measured, repeatable report with sources.

> **STATUS (research, 2026-10-13 — nothing built).** Every number below was measured on the
> Prinsengracht copy `demo_out/plan12/prg1002_sheet_test.rvt` (opened in a separate Revit 2023
> instance with add-in v1.2.8, never saved) and its project folder. Probes are throwaway, under
> `.verify/compare/` (gitignored): `dump.cs` (exec: model footprints + DWG chains → JSON),
> `analyze.py` (grid index + distance statistics), `pdfprobe/` (PdfPig 0.1.11 vector census),
> `pdfreg.py` / `pdfreg2.py` (PDF viewport → model registration), `gen/` (PdfPig-written PDF
> fixture), `synthetic_bestek.pdf` (quantity table through the real `VibeModel.PdfText.exe`).
> Builds on plan 07 (`imports` / `layers` / `curves`, commit 79f0311), plan 10 (`docs` /
> `readdoc`, PDF helper) and plan 08's journal + `[bron: …]` citations.
>
> Prior attempts: none found — `git log --all -i --grep=compare` has no compare work; plan 07
> stopped at extracting curves.

## TL;DR — three verdicts

| Command | Verdict | Why | Size |
|---|---|---|---|
| `compare dwg` | **Feasible — build first** | The DWG and the model already share one coordinate frame (`curves` returns model mm). On the BGT quay layer a 250 mm tolerance cleanly separates "same line" from "different"; the decision must be coverage per run, not Hausdorff per polyline. All of it runs in ≈1–2 s. | M |
| `compare pdf` | **Feasible with what we ship — phase 2** | PdfPig (Apache-2.0, already inside `VibeModel.PdfText.exe`) exposes every path with coordinates, stroke width and colour, plus clipping paths and positioned words. Scale comes from each view's `schaal: 1 : N` label, the viewport from its clip rectangle. Registration (rotation + shift) is the hard part: automatic fit worked here, but needs anchors to be trustworthy. No new library, no licence change. | L |
| `compare spec` | **Feasible as a structured signal — never proof** | Table rows survive PdfText on one line, so regex per quantity line works; the hard part is naming and units, which the command must surface, not guess. The user confirms the type mapping; the result cites page numbers and lands in the journal. | M |

Order: `compare dwg` → `compare spec` → `compare pdf` (the PDF one reuses the DWG matcher and
needs the most UI for anchoring).

---

## 1. What was measured

### 1.1 The probe model

- 793 elements in plan-relevant categories (63 walls, 14 floors, 574 foundations — 510 timber
  piles + 64 sheet-pile/corner instances, 20 framing, 120 generic models, 2 grids), all phase
  "1. Bestaande situatie". The model was traced from the existing situation, so the BGT map
  is the natural reference.
- Three linked DWGs (`imports`): the BGT map 1542105 (9,851 curves, 35 layers), a large
  situation map 1586912 (190,583 curves, 51 layers) and a cables-and-pipes map 1594006
  (1,167 curves, 32 layers). The cables-and-pipes map is the negative control — nothing in it
  should match a quay wall.
- Model plan footprints: the edges of each solid's downward-facing planar faces (via
  `get_Geometry`), plus location curves/points and grid curves → 27,520 plan segments in
  **267 ms**. `DwgGeometryHelper.GetChains` on the 190k-curve import: **1,119 ms**; the bbox
  prefilter over all of them: 56 ms.

### 1.2 Faces, not location lines

BGT `KADEMUUR` samples (every 250 mm) inside the quay-wall area, distance to the nearest wall edge:

| Model geometry | median | ≤ 50 mm | ≤ 100 mm | ≤ 250 mm |
|---|---|---|---|---|
| Wall location lines | 327 mm | 7 % | 25 % | 35 % |
| Wall footprint faces | 55 mm | 46 % | 57 % | 63 % |

(The remaining 37 % are more than 6 m from any wall — stretches of quay the model does not
cover; see §2.3 for why the comparison area matters.) Drawings show faces; the location line
is the wall's centre. **The matcher uses footprint edges.**

### 1.3 Tolerance — where "same line" ends

Restricted to samples within 6 m of a wall face (the modelled area):

| DWG layer (samples) | ≤ 50 | ≤ 100 | ≤ 250 | ≤ 500 |
|---|---|---|---|---|
| BGT `KADEMUUR` (1,917) | 69 % | 85 % | 94 % | 95 % |
| BGT building outlines (51) | 33 % | 35 % | 41 % | 45 % |
| Cables: medium-voltage, runs along the quay (771) | 2 % | 5 % | 10 % | 14 % |
| BGT semi-paved surface (1,690) | 2 % | 4 % | 6 % | 9 % |
| Fifteen other BGT / cable layers | ≤ 1 % | ≤ 2 % | ≤ 4 % | ≤ 11 % |

Distance histogram of all `KADEMUUR` samples inside the quay bbox: 1,811 within 250 mm, then
only **45 between 250 mm and 4 m**, then 1,005 beyond 6 m. The curve is flat after 250 mm —
that is the separation. The 45 in-between samples are exactly the interesting cases (a wall
end modelled 1.5–3.6 m away from where the map has it).

Reverse direction (model → drawing), per quay wall: the best-matching face has a median
distance of 0–80 mm to the BGT line (tracing accuracy of this model); the other face 45–300 mm
(the BGT quay polygon has its own width). One wall (a 34.7 m stretch) has no BGT line near it at
all, and one 4 m wall sits 3.6 m off — both real findings.

Per **chain** (a DWG polyline), "matched" when ≥ 80 % of its samples are within tol:

| tol | quay chains matched (of 9 in the area) | unrelated chains falsely matched (of 148) |
|---|---|---|
| 50 mm | 0 | 0 |
| 100 mm | 3 | 0 |
| 250 mm | 5 | 1 (a 1.4 m line on layer `0`, plausibly the same edge) |

The 4 quay chains unmatched at 250 mm are short pieces (2–6 m) 1.5–5 m from the nearest wall —
genuinely different. **Decision: default `--tol 250` for survey-grade sources (BGT, K&L), and
report the actual deviation of every match so 5 mm and 230 mm are visibly different.** For a
DWG exported from the same design, `--tol 25` is the expected setting — untested, no such DWG
in the probe model; fixtures cover it.

### 1.4 Hausdorff vs sampled nearest distance

A 39.8 m BGT quay chain had 90 % of its samples within 250 mm but a directed Hausdorff distance
of **2,040 mm**: the drawn line continues 4 m past where the modelled wall ends. Hausdorff per
polyline would call the whole line "different". **Decision: sampled nearest distance, classify
each sample, then split each chain into runs** ("36 m matched at ≤ 80 mm, 4 m drawing only").
The maximum distance is still reported per matched run (a partial Hausdorff), never used as the
whole-chain verdict.

### 1.5 The drawing PDF

`PRG1002- DO.pdf` (A0, one page, Adobe PDF Library) through PdfPig 0.1.11 `page.Paths`:
119,363 paths, 227,446 straight segments, **0 béziers** (the exporter flattened arcs), 3,293
closed subpaths, 14 clipping paths, 20,067 words, 151 images; 7 line widths, at least 8 stroke colours;
**no optional-content groups** (no PDF layers). Parsing the page with paths: **12.8 s**.

- 12 view labels `<title> / schaal: 1 : N` (scales 1:20 … 1:5000); the sheet's own "Schaal:"
  field is empty because the sheet mixes scales. 5 views have a clipping rectangle whose bottom
  edge sits just above their label — that rectangle is the viewport.
- The plan view's clip (1:200) holds 20,780 segments. Rotation from a length-weighted direction
  histogram (the view is turned ~13° on the sheet), shift from an FFT cross-correlation of 500 mm
  occupancy grids, then ICP on lines longer than 2 m: **median residual 9 mm, p90 83 mm**; a free
  scale converged to 0.9998 × the label's 1:200. Quay-wall faces → PDF long lines: 75 % within
  50 mm, median 14 mm.
- But ICP **slides along the quay** (~25 mm per iteration): one long straight feature does not
  pin the along-axis shift. Automatic registration worked here only because the drawing was made
  from this model; an independent drawing needs anchors (§3.3).
- All PDF lines vs the model: only 34 % within 50 mm — the sheet also carries piles, hatching,
  dimensions and text. The meaningful direction for a PDF is **model → drawing** ("does every
  modelled edge appear on the drawing?") plus long drawing lines → model.

### 1.6 Text for the specification

- There is **no specification (bestek) in the Prinsengracht folder** — only the DO drawing
  (5,231 text lines: view titles, levels, profile names; no counts, no "st", no "lg.") and a
  memo (33 lines). So the quantity pipeline was probed on a generated PDF.
- `synthetic_bestek.pdf` (a 5-row quantity table with position codes, descriptions, Dutch
  numbers `1.284,5`, units `m2 / st / m1`, and a two-column prose page) through the deployed
  `VibeModel.PdfText.exe`: **every table row stays on one line** (`22.20.01 Bout M24 8.8 … 312 st`),
  the two columns come out in column order, `Ø` survives.
- A naming trap is already present in the real folder: the drawing and the memo name
  **different profiles of the same sheet-pile series**, and the model's type names use a
  different spacing/format again. A naive matcher would either miss all of them or match the
  wrong one — this is the case `compare spec` must show as "needs confirmation".
- Side finding: `filter foundations --count Type` groups by **ElementId** (`ElementId(1571611): 390`)
  instead of type names, and `filter foundations "Type Name" contains AZ` finds nothing (quoted
  parameter names are not parsed). The model side of `compare spec` cannot rely on `filter`; fix
  `filter` separately (small, independent).

---

## 2. `compare dwg`

```
compare dwg <importId> [--layer <name>[,<name>…]] [--category walls,floors,foundations,…]
            [--tol <mm>] [--near <mm>] [--bbox x1 y1 x2 y2 | --view] [--color] [--mark] [--clear]
```

### 2.1 Inputs

- **Drawing side:** `DwgGeometryHelper.GetChains`, layer resolution exactly as `curves`
  (exact, else unique substring; several layers comma-separated). Without `--layer`: every
  layer, but the output ranks layers by matched length so the chat sees which layers *are*
  the quay, the building line, the piles.
- **Model side:** a new `FootprintHelper` returns per element a list of plan segments and an
  optional point: edges of downward- *and* upward-facing planar faces (top and bottom outline —
  a battered wall or a capstone overhang differs between the two), location point for
  point-based families, the curve for grids. Default categories: walls, floors, structural
  foundations, framing, columns, generic models, grids. Elements are those **visible in the
  active view** (so phase filter, worksets, hide and design options apply the way the user sees
  them); `--all` takes the whole document.
- **Point elements (piles):** model centre vs the centroid of small closed DWG loops
  (≤ 2 m across — a pile symbol is a circle or square once blocks are exploded). One-to-one
  greedy pairing by distance. Not probed (no pile DWG in the model) — fixtures only.

### 2.2 Algorithm

1. **Area.** The comparison area is `--bbox`, else the active view's crop box when it is
   cropped, else the largest cluster of the compared elements (reuse `SheetLayout.FitKeep`, the
   `crop --fit` cluster logic) + `--near`. Drawing outside the area is counted, never reported
   as a difference — otherwise a BGT map of the whole canal turns into hundreds of "drawing only"
   lines (37 % of the quay layer in the probe).
2. **Index.** Model segments into a uniform grid hash (cell 2 m; ring search up to `--near`).
3. **Drawing → model.** Sample every chain every `clamp(tol, 100, 500)` mm; per sample the
   nearest model segment (distance + element id). Class per sample: `matched` ≤ tol,
   `offset` ≤ `--near` (default 2,000 mm), else `drawing only`.
4. **Runs.** Consecutive samples of one class form a run; runs shorter than 1 m merge into
   their neighbours. Each run reports length, class, median and max distance, and the element
   it is nearest to.
5. **Model → drawing.** Sample each element's footprint edges; the element is `matched` when
   its best edge (longest matched run) covers ≥ 50 % of that edge's length — one matching face
   is enough (§1.3: drawings often show only one face). Else `offset` / `model only` as above.
   Points: paired / unpaired.
6. **Layer summary.** Per layer: matched / offset / drawing-only length (m), and per category
   matched / offset / model-only element counts.

### 2.3 Output

Text (default) — compact, then the worst findings first. The numbers are the probe's
(BGT `KADEMUUR` + layer `0` vs the 32 concrete quay walls, area = their bbox + 2 m); the
drawing-only run is illustrative:

```
COMPARE: import 1542105 (BGT_…dwg) vs 32 walls in view "__WCS_N.A.P."   tol 250 mm, near 2000 mm
Area: wall cluster + 2000 mm (227 x 141 m); 1,128 m of drawing outside the area ignored

Layer B-BH-GK-KADEMUUR-GV   matched 445 m (median 56 mm, max 215 mm) · offset 16 m · drawing only 255 m
Layer 0                     matched 4.2 m (median 105 mm) · offset 4.4 m
Walls (32)                  matched 30 · offset 1 · model only 1

Model only:  1728711  Beton_Metselwerk_650  34.7 m — no drawing line within 2000 mm
Offset:      1730910  Beton_300  4.0 m — 3.6 m from layer KADEMUUR
Drawing only (longest 10 of N runs, coordinates mm):
  KADEMUUR  128.6 m  from x1,y1 to x2,y2
  …
Next: 'compare dwg … --color' to colour these in the view, 'zoomto <id>' to look.
```

JSON (`?format=json`): `{area, tol, near, layers:[{layer, matchedM, offsetM, drawingOnlyM,
medianMm, maxMm}], elements:[{id, category, type, class, coverage, distanceMm}],
runs:[{layer, class, lengthM, medianMm, maxMm, nearestId, from:[x,y], to:[x,y]}]}` — runs
capped at 200, `truncated` count.

### 2.4 Showing it in the view

- `--color`: `model only` elements red, `offset` orange, through the same per-view override as
  `color` — one transaction, one Ctrl+Z, journaled as a modification. `color <id> reset` or
  `compare dwg --clear` undoes it.
- `--mark`: the DWG is a single element, so drawing-only runs cannot be coloured. `--mark`
  draws them as detail lines in a line style `VM compare` (created on first use) in the active
  plan view; `--clear` deletes every line of that style in the view. Off by default — it adds
  elements to the model.

### 2.5 Performance caps

Measured: 190k chains read in 1.1 s; 27.5k model segments in 0.27 s; 229k samples against the
grid index in 8.1 s **in Python** — the same loop in C# should be well under 1 s (estimate,
not measured). Caps: after the area prefilter at most 20,000 chains and 400,000 samples, else
refuse with the counts and "narrow with --layer / --bbox". The sample step grows (up to 500 mm)
before the cap is hit. Everything runs on Revit's thread (geometry access); the matcher itself
is plain arrays and could move off-thread later if needed.

### 2.6 Files

- `Services/Claude/Commands/CompareCommand.cs` — `compare dwg|pdf|spec`, `IStructuredCommand`;
  `--color/--mark/--clear` paths implement the modification marker.
- `Services/Claude/Compare/PlanMatcher.cs` — Revit-free: segment/point sets, grid index,
  sampling, classes, runs, summaries (all tests run here).
- `Services/Claude/Compare/CompareArgs.cs` — parsing (next to `SourceDataArgs`).
- `Services/Helpers/FootprintHelper.cs` — element → plan segments / point (Revit).
- `Services/Chat/StatusVerbs.cs` — the step label in **both** maps (a test checks).
- `CLAUDE.md` (command section), `README.md` §7, `CHANGELOG.md`.
- Tests: `PlanMatcherTests.cs`, `CompareArgsTests.cs`.

### 2.7 Risks

- **False matches** where unrelated lines run parallel within tol (a cable along the quay: 10 %
  of its samples within 250 mm). Mitigated by the run/coverage rule (0–1 of 148 unrelated
  chains matched) and by `--layer`; the per-layer summary makes a wrong layer obvious.
- **Wrong reference:** a BGT map shows the top of a structure, a design DWG the foundation.
  The output names the faces used (top and bottom outline); `--tol` is the user's lever.
- **DWG not in the model's coordinates** (imported origin-to-origin) — then everything is
  "offset". Detect: if > 80 % of the area's samples are `offset` with one dominant shift, say so
  and print that shift instead of 500 findings.
- **Non-prismatic elements** (in-place families, sloped sweeps): face-edge footprints are
  partial; `ExtrusionAnalyzer` is the fallback for a true plan silhouette (untested).

### 2.8 Tests

Synthetic, no Revit: chains and footprints as coordinate lists into `PlanMatcher`.
A wall pair offset by 0 / 40 / 240 / 260 / 1,500 / 5,000 mm (class boundaries); a line
overshooting the wall end by 4 m (one matched + one drawing-only run, max distance not the
verdict); a parallel decoy line 180 mm away covering 10 % of the length (not matched); points
vs circles (pairing, one-to-one); area clipping (outside not reported); the cap refusal; a
whole-drawing shift (detected as a frame problem). Live verification: the probe model, BGT
`KADEMUUR` vs walls, must reproduce §1.3 (5 matched chains at 250 mm, wall 1728711 model-only).

---

## 3. `compare pdf`

```
compare pdf <document> [--page N] [--view "<view title>" | --scale 200] [--anchor <pdf> = <model> …]
            [--category …] [--tol <mm>] [--color] [--overlay]
```

### 3.1 Feasibility

Yes, with PdfPig (Apache-2.0) — already shipped in `VibeModel.PdfText.exe`, compatible with this
repo's MPL-2.0. `page.Paths` gives each path's subpaths as Move / Line / Bézier / Close commands
in PDF points, with `LineWidth`, `StrokeColor`, `IsStroked/IsFilled/IsClipping`; `GetWords()`
gives words with bounding boxes. The probe used PyMuPDF only for convenience in Python — it is
AGPL and must **not** be shipped; nothing in the pipeline needs it. If PdfPig ever chokes on a
file, PDFium (BSD-3/Apache-2.0) is the licence-safe fallback, at the cost of a native DLL.

### 3.2 Pipeline

1. **Extract (out of process).** PdfText gets a second mode:
   `VibeModel.PdfText.exe --vectors <pdf> <out.json> [--page N]` →
   `{pages:[{w, h, clips:[[x0,y0,x1,y1]], labels:[{text, bbox}], styles:[{width, color}],
   segs:[x1,y1,x2,y2,style, …]}]}` with béziers flattened to ≤ 0.1 mm chord error and
   segments shorter than 0.2 mm paper dropped. 12.8 s for the A0 → cached exactly like `readdoc`
   (`%LOCALAPPDATA%\VibeModel\projectdocs\_cache\`, keyed by path + size + mtime), same
   "still extracting, ask again" behaviour after 12 s.
2. **Viewport + scale.** Find labels matching `schaal\s*:?\s*1\s*:\s*(\d+)` (and `scale 1:N`);
   the view title is the label line above. The viewport is the clipping rectangle directly
   above the label; without one, the bbox of the segment cluster above the label. `--view`
   picks by title, `--scale` overrides N. Paper mm = pt × 25.4/72; model mm = paper mm × N.
3. **Register.** Similarity transform without scale (scale is known): (a) `--anchor` pairs, two
   or more, each `pdf <label text | x,y paper mm> = model <grid A/1 | element id | x,y>` →
   least squares; (b) without anchors, the automatic fit from the probe (direction histogram →
   FFT correlation → ICP on long lines) **as a proposal only**: the command writes an overlay PNG
   (`--overlay`, PDF lines over model edges, like `.verify/compare/overlay.png`) and reports the
   residual and the along-axis uncertainty. Grid bubbles are the best anchors (their labels are
   words on the sheet, their intersection is a model point).
4. **Compare.** The transformed segments go into the same `PlanMatcher` as `compare dwg`, with
   style groups (width + colour) taking the place of layers, model → drawing as the primary
   direction, and drawing → model for segments longer than 2 m only.

### 3.3 Risks

- **Registration is the result.** A 20 mm along-axis error is invisible on a long straight quay
  and decisive at its ends. Without anchors the output must say "registration: automatic,
  along-axis uncertain" on its first line, and the colours (`--color`) are only offered after
  anchors or a confirmed overlay.
- **Content density:** hatching, pile symbols, dimension lines and text produce near misses
  everywhere (34 % of all PDF lines within 50 mm of *something*). Style filtering and the
  long-line rule are essential; text-as-geometry PDFs (SHX) need the < 0.2 mm-paper filter
  plus a cap.
- **Scanned / raster drawings:** no paths, only images → refused with "raster only (N images),
  no vector comparison possible". No OCR, no raster tracing.
- **Older revision:** a DO drawing is a snapshot. The report names the PDF's date (file mtime /
  title block) next to the model stamp.

### 3.4 Files and tests

`VibeModel.PdfText/Program.cs` (vectors mode), `Services/Claude/Compare/PdfVectors.cs`
(read/cache the JSON, viewport + scale detection, transform — Revit-free),
`Services/Claude/Compare/Registration.cs` (anchors least squares, automatic proposal),
`CompareCommand` pdf branch, overlay PNG writer. Tests: **PdfPig's own `PdfDocumentBuilder`
writes the fixtures** (verified in the probe: `DrawLine` / `DrawRectangle` / `AddText` with a
Standard-14 font round-trip through `page.Paths` and `page.Text`) — a plan with known lines, a
rotated viewport, a `schaal: 1 : 200` label, a clip rectangle; assert scale, viewport and a
registration error below 1 mm with two anchors. Size **L**.

---

## 4. `compare spec`

```
compare spec <document> [--items "bout M24, damwand AZ26"] [--map "<item>=<type>[|<type>]" …]
             [--category …] [--pages 3-12]
```

### 4.1 Pipeline

1. **Text.** `readdoc`'s cached text (PDF via PdfText, `.docx`, `.txt`), split on the existing
   `--- page N ---` markers so every hit keeps its page.
2. **Quantity lines.** A line is a quantity line when it ends in a number + unit:
   `(?<qty>\d{1,3}(?:\.\d{3})*(?:,\d+)?|\d+(?:[.,]\d+)?)\s*(?<unit>st|stk|stuks|pcs|m1|m¹|m|m2|m²|m3|m³|kg|ton|t)\b`
   (Dutch thousands dots and decimal comma first; ambiguous `1.284` read as thousands only
   with three trailing digits and a Dutch-format document). Optional leading position code
   (`\d{2}(\.\d{2}){1,3}`). Product tokens are tagged: profiles (`AZ\s?\d+(-\d+)?`, `HE[ABM]\s?\d+`,
   `IPE\s?\d+`, `UNP`, `PU`), bolts (`M\d{2}` + grade `\d\.\d`), diameters (`Ø\s?\d+`), lengths
   (`lg\.?\s?\d+[.,]?\d*\s?m`).
3. **Items.** Without `--items`: list the detected quantity lines (page, position, description,
   qty, unit — first 50) so the chat or the user can choose. With `--items`: each item becomes
   normalised tokens (case, spaces and hyphens folded: `AZ 26-700` = `AZ26-700`); a spec line
   matches when it contains all tokens. Several matching lines are listed separately, **never
   summed** (a total in a summary chapter and the same item per section would double) unless
   `--sum`.
4. **Model side.** Its own grouping by family + type name (not `filter`, §1.6), normalised the
   same way; the quantity follows the spec unit: `st` → count; `m`/`m1` → summed length
   (location curve or the `Length` parameter); `m²` → summed `Area` when the category has it;
   `kg`/`ton` and `m²` of sheet piles (count × system width × length) → **not comparable**
   unless the user maps a parameter. The match list of types is always printed.
5. **Fuzzy step = the chat.** The command never accepts a fuzzy match. It prints candidates
   ("spec: AZ26-700 · model types: AZ 24-700 (30), Damwand AZ12-AZ32 (30) · token mismatch
   26≠24"); the user confirms with `--map`, which the chat can then `remember` so the next
   compare in this project uses the same mapping.
6. **Result table** (markdown in the chat, per the RULES block):

   | Item | Spec | Model | Δ | Status | Source |
   |---|---|---|---|---|---|
   | bout M24 | 312 st | 300 st (2 types) | −12 st | differs | [bron: document bestek.pdf p.14] |

   Statuses: `equal`, `differs`, `not comparable (unit)`, `ambiguous` (several spec lines or
   unconfirmed types), `not in spec`, `not in model`. Model cells cite `[bron: revit <type id>]`.
7. **Journal.** `compare` is read-only, and today only modifications and `export`
   (`IJournaledCommand`, files written) are journaled. Add a small `IJournalSummary`
   (`string JournalSummary(CommandResult)`) honoured next to `IJournaledCommand` in
   `RevitCommandHandler`: `… · compare spec bestek.pdf · 3 equal, 1 differs (bout M24 312/300 st),
   1 ambiguous · chat t3f9a2c "…"`. `compare dwg/pdf` use it too.

### 4.2 Signal vs proof

The output always opens with **"Signal, not proof"** and lists the assumptions that apply.
A line may be called `equal` only when all hold; anything else says which one failed:

1. **One spec line** for the item (or an explicit `--sum`), on a page that had text (a page
   with no text = scanned → "page N has no text; not read").
2. **Confirmed type mapping** — every model type counted was matched exactly by tokens or
   confirmed via `--map`; never by similarity alone.
3. **Same unit, no conversion assumption** (count vs count, length vs length).
4. **Complete model set** — the categories asked, elements in groups and design options
   counted (or excluded and said so); linked models are *not* counted and the output says so
   when links exist.
5. **Same revision** — the document's date and the model stamp are printed side by side.

What the user must confirm: the type mapping, which spec line is authoritative when there are
several, and every `not comparable` unit. Proof stays a human signature; the command makes the
evidence (page, line, element types, counts) one tap away.

### 4.3 Risks

- **Naming** (the real trap in §1.6): exact-token matching plus forced confirmation is slower
  but never silently wrong.
- **PDF text order:** PdfText kept table rows intact on the generated table; other producers
  split cells over lines. Fallback: when a description line has no quantity, join it with the
  next line if that one is a bare `qty unit` (tested).
- **Dutch numbers** (`1.284,5` vs `1,284.5`): format detected per document from the majority.
- **Quantities that are not counts in the model** (m² sheet pile, kg steel) — reported as not
  comparable instead of a wrong number.

### 4.4 Files and tests

`Services/Claude/Compare/SpecExtractor.cs` (pages → quantity lines → items; Revit-free),
`Services/Claude/Compare/ModelQuantities.cs` (Revit: type grouping and quantities),
`CompareCommand` spec branch, `IJournalSummary` in `IClaudeCommand.cs` +
`RevitCommandHandler`, `ProjectJournal` formatting. Tests: `SpecExtractorTests` on text
fixtures and on a PDF written with `PdfDocumentBuilder` then run through the real extractor
(table, two columns, Dutch numbers, a split row, a page without text, a total that must not be
summed); token normalisation (`AZ 26-700` = `AZ26-700`; item `M24` matches `Bout M24 8.8` by
containment and the grade is shown, item `M24 10.9` does not); unit compatibility table. Size **M**.

---

## 5. Order, size and what is out of scope

1. `compare dwg` (M) + `IJournalSummary` + the `filter --count Type` fix (S, independent).
2. `compare spec` (M).
3. `compare pdf` (L): PdfText vectors mode → viewport/scale → anchors → automatic proposal +
   overlay.

Out of scope: OCR of scanned PDFs, raster tracing, comparing sections/elevations (plan only —
heights need a different reference), comparing against linked Revit models (a different and
easier problem: element-by-element), and writing anything back into the specification.
