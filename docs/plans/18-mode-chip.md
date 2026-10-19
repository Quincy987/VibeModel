# Plan 18 — mode chip in the chat input, "next message as preview"

> Goal: switching how the AI's changes are handled (plan 15: auto / ask / read-only) should not
> need Settings. Cursor puts its agent mode in a chip at the bottom-left of the input box; the user
> picked that layout (mockup A, `demo_out/t352/mockups/A_cursor_footer.png`, not in the repo).

> **STATUS (built 2026-10-20, branch claude-bot/t-360, on top of plan 15's claude-bot/t-352).**
> 1500 headless tests; compiles for Revit 2022/2023/2024; live in Revit 2023 — see "Verification".

## Decisions

- **Layout.** The input is one rounded box (`BgInput`, radius 8): the TextBox without its own border
  on top, a footer below with the paperclip (unchanged handler), the chip, a spacer and a round send
  arrow (Segoe MDL2 `E724`). *Cancel* takes the arrow's place during a turn, as before. The send
  button keeps the UIA name "Send" (the demo scripts find it by name) and gains AutomationId
  `SendButton`.
- **Chip.** A Button drawn as a pill (so UIA can invoke it): mode icon in the mode's colour
  (*Automatisch* teal `E945`, *Vragen* amber `EA18`, *Alleen lezen* grey `E7B3`; a coloured dot when
  Segoe MDL2 Assets is missing), the mode name and a chevron. It shows the mode **in force**: *Vragen*
  with the approval window's "Deze Revit-sessie niet meer vragen" ticked shows *Automatisch*. In
  *Vragen* the send arrow is amber. Tooltip: `ChangeApprovalPolicy.Describe` ("ask (this session)").
- **Menu.** A WPF Popup above the chip (StaysOpen=false, Esc closes): three rows with icon, bold name,
  one-line description and a check on the current mode, then a divider and a checkbox *Volgend bericht
  als preview* / *uitvoeren, rapporteren, terugdraaien*. The checkbox listens to Checked/Unchecked, not
  Click — UI Automation's Toggle does not raise Click (found live).
- **Session override, not a setting.** A pick sets `ChangeApprovalPolicy.SessionOverride` (a volatile
  int, -1 = none) for the rest of the Revit session; settings.json is not written. Precedence:
  managed.json → chip → `VIBEMODEL_APPROVAL` → settings.json → auto (`WithSessionOverride`,
  `EffectiveOf`, `Chosen`; `EffectiveMode` goes through it). Every reader of the policy already used
  `EffectiveMode` (RevitCommandHandler, RevitHttpServer, ChatBackendBase, ClaudeCodeBackend,
  FileBasedFallback — checked by grep), so the server and the prompts follow without further change.
  `ChangeApprovalSource.Session` names it in Describe, Diagnose and `approvals` (JSON `"session"`).
- **"Don't ask again" vs the chip.** The plan said "override Ask with DontAskThisSession → Ask".
  Built instead: `EffectiveOf` turns Ask into Auto under "don't ask again" whichever way Ask was
  chosen (otherwise the window's checkbox would do nothing after a chip pick), and choosing *Vragen*
  in the chip clears "don't ask again" (`ChooseForSession`) — so picking *Vragen* does mean asking.
- **The chip follows changes made elsewhere.** `ChangeApprovalPolicy.Changed` fires when
  "don't ask again" or the override really changes; the pane refreshes the chip on it (live, the first
  build only refreshed on tracker changes and missed the window's checkbox), and also after Settings
  closes, on `ActiveDocumentTracker.Changed` and when a turn ends.
- **Locked by IT.** managed.json sets the mode → the chip is greyed, has no menu, tooltip "… beheerd
  door IT" (`Strings.ModeChipManaged`).
- **Settings.** The approval panel shows the chip's choice as a ticked box ("Deze Revit-sessie: Vragen
  (gekozen in de chat) - vink uit …"); untick + Save clears the override. A box rather than the plan's
  "Wis" button: it matches the "don't ask again" box right above it and keeps Save/Cancel meaning what
  they mean.
- **Preview turn.** The pane calls `ChangeApprovalPolicy.ArmNextTurn(previewNext)` on EVERY send (so a
  send that failed before a turn started cannot leave the next turn previewed) and clears the toggle.
  `ChatBackendBase.RunJournaledTurn` — the wrapper every backend runs a turn in — takes it
  (`BeginTurn`) and clears it in its finally (`EndTurn`), independent of the journal switch.
  `RunOptions.ForTurn` merges it into both enqueue paths, so every modifying request in that turn
  runs as `?preview=1` (a new RunOptions; the shared `None` and the request's own options are never
  changed). Like the journal's turn attribution, an external curl made during the turn is previewed
  too; a request enqueued before the turn began is not (seen live: a curl fired right after Send ran
  before the CLI started). The file-based fallback refuses changes during a preview turn. The rules
  block gets `PreviewTurnRule` only in a preview turn — prompts are byte-identical otherwise (tests).

## Verification

**Headless:** `ModeChipTests` (precedence matrix, Describe, `RunOptions.ForTurn`, the preview rule only
when previewing for every mode and in the Claude Code turn prompt, mode names and tooltips in both
languages, the statics: choosing Ask clears don't-ask, managed.json beats the chip, the turn flag is
taken once and cleared, `Changed` fires once per real change). 1500 tests pass.

**Live, Revit 2023, 2026-10-20 00:20–00:28, a copy of the quay-wall demo model, approval default,
UIA invoke only** (`%LOCALAPPDATA%\Temp\t360verify\chip.py`, `preview_turn.py`): chip present and
*Automatisch*; send arrow found by the name "Send"; the menu opens with the four rows; *Vragen* →
chip *Vragen*, amber arrow, `approvals` = `ask (this session)`, a curl `delete` opens the approval
window (Discard → `ERROR: Discarded by the user.`); *Automatisch* → the same delete without a window;
*Alleen lezen* → a `wall` is refused (`READ_ONLY`); *Vragen* again + "niet meer vragen" ticked in the
window → the chip shows *Automatisch* (after the `Changed` fix); Esc closes the menu; the preview
toggle shows the badge *voorbeeld* and is gone after Send. **A real chat turn** (Claude Code CLI) with
*Volgend bericht als preview* on: "Verwijder wand 1730914." — the turn prompt carried the PREVIEW rule,
the CLI ran `delete 1730914` (log 00:27:38), the wall was still there afterwards, no journal line
deleted it, and the answer began "I tried it as a preview, so nothing changed." 16/18 + 4/5 checks
first time; the three failures were the two bugs fixed above and a check of mine that matched an
older journal line for a reused element id. Screenshot: `demo_out/t352/mockups/live_pane.png` (not in
the repo).

Not verified live: the greyed chip under managed.json (unit-tested via `Chosen`/`ModeChipTip`), the
Settings checkbox (built, compiled, not clicked), the coloured-dot fallback without Segoe MDL2 Assets.
