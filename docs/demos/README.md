# Demo videos – one per topic

One short screen recording per topic from the README's "What it can do" list: Revit full-screen with
the chat panel open, the question typed live, processing fast-forwarded, the result shown live in
the model. No slides. Recorded on a copy of a real project; nothing is saved.

| # | Topic | Video | Length |
|---|-------|-------|--------|
| 1 | Questions | _not recorded yet_ | ~1 min |
| 2 | Information | _not recorded yet_ | ~1 min |
| 3 | Diagnosis | _not recorded yet_ | ~1 min |
| 4 | Modelling | _not recorded yet_ | ~2 min |
| 5 | Sheets | _not recorded yet_ | ~2 min |
| 6 | Annotating | _not recorded yet_ | ~1 min |
| 7 | Checking against a document | _not recorded yet_ | ~1 min |
| 8 | Context | _not recorded yet_ | ~1 min |
| 9 | Exchange | _not recorded yet_ | ~1 min |

Each row links to the video (hosted outside the repository) once it exists.

## Recording rules

- Dry-run every prompt below first on the demo model; record only prompts that work in one turn.
- Capture the Revit window only, 1920×1080, chat panel docked right at ~420 px.
- Speed up the "thinking" part 8× so each video stays near the length above; keep the typing and the
  result in real time.
- No internal paths, document names or colleague names on screen: use the public demo model and the
  example standards pack (`standards/example-office/`).
- Tell IT security before recording if your organisation flags screen capture.

## Scripts (the prompts, in the order they are typed)

Prompts are in Dutch because the first audience is Dutch; the chat answers in the language you use.

### 1. Questions
1. `Wat zit er in dit model?`
2. `Hoe stel ik de view range in zodat ik de fundering zie?`
3. `Wat zegt de kantoorstandaard over fasering? Doe het meteen voor de geselecteerde wand.`

### 2. Information
1. `Hoeveel palen staan er, per type, met lengte? Zet het in een tabel.`
2. `Welke palen hebben een schoorstand groter dan 1:10?`
3. `Lees de laag "pand" uit de DWG-onderlegger en geef de gesloten contouren.`

### 3. Diagnosis
1. `Waarom zie ik element 348122 niet in deze view?`
2. `Join wand 12345 met de deksteen 67890. Als het niet mag: waarom niet, en wat wel?`
3. `Controleer blad 0001: overlappen er views, staat er iets buiten het papier?`

### 4. Modelling
1. `Zet 6 palen langs deze detail line, om de 2800 mm, gedraaid naar de lijn, met Height Offset From Level -2500.`
2. `Maak een doorsnede dwars op wand 12345 en snijd de view bij op de elementen.`
3. `Maak een 3D-view vanaf het zuidwesten op die palen.`
4. `Draai alles terug.` (one Ctrl+Z)

### 5. Sheets
1. `Maak een A0-blad 0002 "Situatie" en vul Drawn By in.`
2. `Zet de plattegrond en de twee doorsnedes passend op dat blad, plattegrond boven.`
3. `Past de doorsnede niet? Welke schaal wel?`
4. `Verplaats doorsnede 2-2 naar blad 0003.`

### 6. Annotating
1. `Zet een peilmatenstring in doorsnede 1-1.`
2. `Maatvoer de twee wanden in de plattegrond.`
3. `Zet de damwandlengte uit het bestek als tekst met pijl bij de damwand.`

### 7. Checking against a document
1. `Hoeveel bouten M24 noemt het bestek, en hoeveel zitten er in het model?`
2. `Waar komt dat aantal vandaan?` (click the source marker)
3. `Laat het journaal van vandaag zien.`

### 8. Context
1. `Welke documenten zitten in de projectmap?`
2. `Onthoud dat nieuwe doorsnedes fase 2 krijgen.`
3. New Chat → `Welke fase krijgen doorsnedes hier?`

### 9. Exchange
1. `Maak een screenshot van blad 0002 zonder het te openen.`
2. `Exporteer blad 0002 als PDF en DWG naar de projectmap.`
