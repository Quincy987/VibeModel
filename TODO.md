# TODO

## Feedback gebruikers — Ruben Wolbers, 12 november 2026
<!-- Bron: demo_out/successcases/interviews/2026-11-12-ruben-wolbers.md (gitignored). Tijdstippen = transcript. -->

- [ ] **Annotatie-layout** (04:24–09:30): tags, maatvoering en teksten op een gegenereerd blad staan over elkaar / scheef;
      de chat meldt de overlap maar lost hem niet op. Onderzoek: welke plaatsing stuurt de API (TagOrientation,
      leader-elbow/end, `TextNote` positie, `Dimension.TextPosition`), en een layout-check/-fix na `tag`/`dimension`/`text`.
- [ ] **Schema's vergeten dingen** (08:50, 12:31): onderwapening wel gemodelleerd, niet in het schematische overzicht.
      Onderzoek: `schedule`-filters/fasen/categorie-afbakening; laat een schema na aanmaak melden welke elementen in
      scope buiten het schema vallen.
- [ ] **Hoeveelheden nooit handmatig** (13:46–15:16): bij ontbrekende m² werden parameters aangemaakt en handmatig gevuld.
      Regel: hoeveelheden altijd dynamisch (berekende waarde / schedule-formule / Material Takeoff); kan het niet, zeg het
      en vraag hoe. Onderzoek: wat kan de API hier wel (calculated fields, shared parameter met formule in familie).
- [x] **Aannames vooraf voorleggen** (13:34–14:50): bij een bouwopdracht vanaf nul eerst de aannames (fase, type,
      niveau, afmetingen die niet in de vraag staan) opsommen en bevestiging vragen voordat gemodelleerd wordt. → `ChatBackendBase.AssumptionsFirstRule`
- [ ] **Wit thema** (35:23): pane volgt Revit niet; alleen een donker palet (`UI/Palette.cs`). Licht thema + instelling
      (of Revit's UI-thema volgen).
- [ ] **Chat-export** (10:08–12:08): export van de chatgeschiedenis + aangeroepen tools als tekstbestand (Settings/Diagnose),
      zodat gebruikers hem kunnen opsturen; bestaat nog niet (alleen `chatlog` en het chat-JSON).
- [ ] **Uitgeklede uitrol** (25:01, 34:25): managed.json-profiel "alleen lezen + vragen" voor de overige modelleurs, plus
      pilot met één persoon.

## Deferred Revisions
<!-- Auto-managed by code review. Remove items when addressed. -->
- [ ] The managed.json key list in the docs needs Theme too (Low)
