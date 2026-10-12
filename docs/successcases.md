# Successcases — twee weken Revit-vragen van werkvoorbereiders

Na de VibeModel-workshop van 12 oktober 2026 mogen de werkvoorbereiders twee weken lang hun
Revit-problemen mailen. Elk probleem wordt een **successcase**: de vraag, het antwoord, hoe goed
dat antwoord was en hoe snel we bij de oplossing waren. De verzameling is het materiaal voor de
vervolgpresentatie en voor een latere UX-video.

## Protocol per mail

1. **Mail komt binnen.** Maak er meteen een case van; afzender wordt *Wie*, de mailtekst wordt
   *Vraag* (de geciteerde mailgeschiedenis eronder valt weg):
   ```
   python -I scripts/successcase_mail.py "<deel van het onderwerp>"
   ```
2. **Reproduceer in VibeModel.** Open het model en stel de vraag in het chatpaneel. Begin per
   probleem een **nieuwe chat** — een chat is één case, en het antwoord in de case is het
   laatste antwoord uit die chat.
3. **Koppel de chat.** De chat levert Antwoord, Verloop (per beurt: tijdstip, seconden tot het
   antwoord, aantal commands) en de totale tijd:
   ```
   python -I scripts/successcase_mail.py "<onderwerp>" --chat <chat-id>
   python -I scripts/successcase_mail.py --case demo_out/successcases/<case>.md --chat <chat-id>   # zonder Outlook
   ```
   Het chat-id is de bestandsnaam in `%LOCALAPPDATA%\VibeModel\chats\` (bijv.
   `20261012-101908-725070b0`). Een vraag die niet per mail kwam: `python -I scripts/successcase.py <chat-id>`.
4. **Vul Kwaliteit in** (en eventueel Opmerkingen) in het casebestand:
   `Score (1-5): 4` — 5 = correct en direct bruikbaar, 3 = bruikbaar na correctie,
   1 = fout/misleidend.
5. **De index** (`demo_out/successcases/INDEX.md`) wordt bij elke run opnieuw opgebouwd: één regel
   per case met datum, model, vraag, totale tijd en kwaliteit, plus het gemiddelde. Alleen de
   index opnieuw maken: `python -I scripts/successcase.py --index-only`.

Alle mails sinds een datum in één keer (kijk eerst met `--dry-run`, anders wordt élke mail een case):
```
python -I scripts/successcase_mail.py --since 2026-10-12 --query revit --dry-run
```

## Wat waar staat

- **Casebestanden:** `demo_out/successcases/<datum>-<slug>.md` (andere map: `--out`). `demo_out/`
  staat in `.gitignore`: de cases bevatten namen van collega's en worden nooit gecommit.
- **Opnieuw draaien is veilig.** Wie, Score en Opmerkingen blijven staan; de rest (Antwoord,
  Verloop, tijden, Model) wordt ververst. Een case wordt teruggevonden via het chat-id of de mail
  in de verborgen `<!-- successcase … -->`-regel; die regel niet weghalen.
- **Tijden.** *Totale tijd* is de doorlooptijd van eerste vraag tot laatste antwoord (inclusief
  de tijd dat de gebruiker zelf keek of typte), met daarachter hoeveel daarvan VibeModel aan het
  werk was.
- **Commands** staan niet in de chatlog; ze worden geteld uit de VibeModel-debuglog
  (`%LOCALAPPDATA%\VibeModel\logs\log_<datum>.txt`) tussen vraag en antwoord. Indicatief: andere
  programma's die op dezelfde pc met Revit praten, tellen mee. Zonder log staat er `n.b.`.
- **Outlook** gaat via de Outlook-helper van de bot
  (`C:\Development\birdybot\bot\services\outlook.py`; ander pad: `--helper` of
  `VIBEMODEL_OUTLOOK_HELPER`). Ontbreekt die of pywin32, dan meldt het script dat in één regel.
  De helper doorzoekt alleen de nieuwste 200 mails in de inbox.

Tests: `python -I -m unittest discover -s scripts/tests -v`
