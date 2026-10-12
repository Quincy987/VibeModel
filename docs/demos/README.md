# Demo recordings – one per topic

One short screen recording per topic from the README's "What it can do" list: Revit with the chat
panel open, the question typed live, processing fast-forwarded, the result shown live in the
model. No slides. Recorded on a copy of a demo project; nothing is saved. Each recording also
yields a short GIF (chat panel only, ≤ 5 MB) that the README embeds next to the matching topic.

| # | Topic | GIF | Video | Length |
|---|-------|-----|-------|--------|
| 1 | Questions | _not recorded yet_ | _not recorded yet_ | ~1 min |
| 2 | Information | [information.gif](../images/information.gif) (28 s): *Give me a table of the 5 longest walls: id, length, height and phase created* | _not recorded yet_ | ~1 min |
| 3 | Diagnosis | [diagnosis.gif](../images/diagnosis.gif) (25 s): *Why can't I see element 1561029 in this view?* | _not recorded yet_ | ~1 min |
| 4 | Modelling | [modelling.gif](../images/modelling.gif) (27 s): *Place 5 Betonpaal 320x320 piles along a line from 0,0 to 12000,0, 3000 mm apart* | _not recorded yet_ | ~2 min |
| 5 | Sheets | [sheets.gif](../images/sheets.gif) (28 s): *Make a pile schedule with count and length and put it on a new A0 sheet* | _not recorded yet_ | ~2 min |
| 6 | Annotating | _not recorded yet_ | _not recorded yet_ | ~1 min |
| 7 | Checking against a document | _not recorded yet_ | _not recorded yet_ | ~1 min |
| 8 | Context | [context.gif](../images/context.gif) (31 s): *Remember that new sections get phase 2* → New Chat → *Which phase do sections get here?* | _not recorded yet_ | ~1 min |
| 9 | Exchange | _not recorded yet_ | _not recorded yet_ | ~1 min |

GIFs live in `docs/images/`; videos are hosted outside the repository and linked here.

The five GIFs were captured from the live add-in (v1.2.8) on a throwaway copy of a quay-wall demo
model, never saved: the Revit window only, drawn off-screen with `PrintWindow` and cropped to the
chat panel, the prompt set and sent through UI Automation (no keystrokes or mouse), 3 frames per
second, the waiting part kept at one frame in nine. Topics 4, 5 and 8 were recorded with *Apply
office standards* switched off and the template's title-block families renamed to neutral names,
so no office-internal family or parameter names appear. Each turn's chat file and step arguments
were searched for internal names before a GIF was kept.

## Recording rules

- Dry-run every prompt below first on the demo model; record only prompts that work in one turn.
- Capture the Revit window only, 1920×1080, chat panel docked right at ~420 px; GIFs are the chat
  panel only, 2–4 frames per second, 15–30 s, ≤ 5 MB.
- Speed up the "thinking" part 8× so each video stays near the length above; keep the typing and
  the result in real time.
- Panel language set to English for anything that ends up in this repository.
- No internal paths, document names, family or parameter names from a real office on screen: use
  the public demo model and the example standards pack (`standards/example-office/`). Check every
  frame before committing.
- Tell IT security before recording if your organisation flags screen capture.

## Scripts (the prompts, in the order they are typed)

### 1. Questions
1. `What's in this model?`
2. `How do I set the view range so I can see the foundations?`
3. `What does the office standard say about phasing? Apply it to the selected wall.`

### 2. Information
1. `How many piles are there, per type, with length? Put it in a table.`
2. `Which piles have a rake steeper than 1:10?`
3. `Read the "pand" layer of the DWG underlay and give me the closed outlines.`

### 3. Diagnosis
1. `Why can't I see element 348122 in this view?`
2. `Join wall 12345 with capstone 67890. If Revit refuses: why, and what works instead?`
3. `Check sheet 0001: do any views overlap, is anything off the paper?`

### 4. Modelling
1. `Place 6 piles along this detail line, 2800 mm apart, turned to the line, with Height Offset From Level -2500.`
2. `Make a section across wall 12345 and crop the view to the elements.`
3. `Make a 3D view looking at those piles from the south-west.`
4. `Undo all of that.` (one Ctrl+Z)

### 5. Sheets
1. `Make an A0 sheet 0002 "Site plan" and fill in Drawn By.`
2. `Put the plan and the two sections on that sheet so they fit, plan on top.`
3. `If the section doesn't fit, which scale would?`
4. `Move section 2-2 to sheet 0003.`

### 6. Annotating
1. `Add a level string to section 1-1.`
2. `Dimension the two walls in the plan.`
3. `Put the sheet-pile length from the specification as text with a leader next to the sheet-pile wall.`

### 7. Checking against a document
1. `How many M24 bolts does the specification mention, and how many are in the model?`
2. `Where does that number come from?` (click the source marker)
3. `Show today's journal.`

### 8. Context
1. `Which documents are in the project folder?`
2. `Remember that new sections get phase 2.`
3. New Chat → `Which phase do sections get here?`

### 9. Exchange
1. `Make a screenshot of sheet 0002 without opening it.`
2. `Export sheet 0002 as PDF and DWG to the project folder.`
