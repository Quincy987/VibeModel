<div align="center">

# VibeModel

**Talk to your Revit model.**

An add-in that puts an AI chat panel inside Revit. Ask in plain language, watch it work, keep what you like. Every change is one Ctrl+Z.

[![Release](https://img.shields.io/github/v/tag/Quincy987/VibeModel?label=release&color=2ea44f)](https://github.com/Quincy987/VibeModel/releases)
[![Revit 2022+](https://img.shields.io/badge/Revit-2022%2B-0696D7)](#requirements)
[![License: MPL-2.0](https://img.shields.io/badge/license-MPL--2.0-blue)](#license)
[![Tests](https://img.shields.io/badge/tests-1004%20passing-success)](#building-from-source)

</div>

https://github.com/user-attachments/assets/0265c762-6a13-4089-b537-b8c73ea619e8

<div align="center"><sub>Three questions, in Dutch: a quay wall as an L-wall on the base map's quay line (wall, foundation slab, capstone), two pile rows under the slab, then a 1:50 section with level marks. Real screen recording; only the wait is sped up 6x (red badge = real seconds). <a href="https://github.com/Quincy987/VibeModel/releases/tag/demo-videos-2026-10">Full videos</a>.</sub></div>

---

## Why

Revit work is full of small questions with slow answers: *why can't I see this beam, how many piles have this rake, put these four views on an A0, what does the office manual say about phases.* VibeModel answers them in the model itself, and does the work when you ask it to.

- **It reads before it guesses.** Visibility settings, DWG linework, point-cloud ground levels, the project's PDFs, the office standards pack.
- **It shows its work.** Every step appears live while it runs. Every fact taken from a document or the standards carries a source marker. Every change lands in a project journal.
- **It stays reversible.** One request is one Revit undo step, even when it took twenty commands.
- **It speaks your language.** It replies in the language you write in; the panel's own texts come in English and Dutch.

## Quick start

1. **Download** `VibeModel-v<version>-Revit2023.zip` from the [latest release](https://github.com/Quincy987/VibeModel/releases/latest) (under *Assets* — not *Source code*) and unzip it.
2. **Close Revit and double-click `install.cmd`.** It finds your Revit versions, copies the files, unblocks them and checks the result. Press Enter at the API-key question if you use Claude Code.
3. **Open Revit**, click **VibeModel → Chat**, and type. An empty chat shows the open model, its project folder and four starter prompts.

Connect it to Claude in one of two ways: a **Claude Code** subscription (recommended, auto-detected) or an **Anthropic API key** pasted into *Settings*. A local LLM works too. Details under [Installation](#installation).


## What you can say

```
What's in this model?
Why can't I see this beam in my project?
Which walls are the longest? Put them in a table with lengths and phases
Put piles along this line, every 2.8 m, turned with the line
Cut a section through this wall and put it on the sheet at 1:50
Make me a pile schedule and export the sheet as a PDF
Remember: new sections get phase 2
```

## What it can do

Nine kinds of work, with an honest status as of v1.2.8. Five topics have a live screen recording folded under the table (Dutch prompts; the full videos are in the <a href="https://github.com/Quincy987/VibeModel/releases/tag/demo-videos-2026-10">demo-videos release</a>); the scripts and the rest live in [docs/demos](docs/demos/README.md).

| | Topic | Status |
|---|---|---|
| 1 | **Questions** — *"How do I set the view range so I can see the foundations?"* | ✅ General Revit, your office manual per chapter, the project's own documents. Tip: don't ask *how*, ask *do*. |
| 2 | **Information** — *"How many piles have this rake?"* | ✅ Count, filter, measure, real Revit schedules; DWG layers and point-cloud ground read directly. 🟡 Only if the data is in the model. |
| 3 | **Diagnosis** — *"Where did my capstone go? Why won't it join?"* | ✅ The strongest part. `whyhidden` walks every visibility gate and names the fix; `join` explains refusals; `check` audits the office rules; `sheetinfo` finds overlaps and empty stamp fields. |
| 4 | **Modelling** — *"Draw a wall along this line and cut a section through it"* | ✅ Walls, floors, families with type, rotation, parameters, placed along a line; sections, crops, aimed 3D views, callouts; one batch = one Ctrl+Z. ❌ Composite profiles, sweeps, in-place families. |
| 5 | **Sheets** — *"Put these views on an A0 so they fit"* | ✅ Sheet, stamp, grid or band layout, scale advice, move between sheets. 🟡 You decide the layout. ❌ Legends (the API can't create them). |
| 6 | **Annotating** — *"Add the levels and the sheet-pile length from the spec"* | ✅ Dimensions, level strings, text with leaders, tags, filled regions, detail items. 🟡 From a specification: a first pass. ❌ Keynotes, dimensions in 3D. |
| 7 | **Checking against a document** — *"Does the number of bolts match the specification?"* | 🟡 Reads the PDF, counts, compares, cites sources, journals the result. Still a signal, not proof. ❌ Automatic drawing-vs-model comparison. |
| 8 | **Context** — what it knows about your project and office | ✅ Project folder, office standards pack, project memory across chats, journal of changes. |
| 9 | **Exchange** | ✅ Screenshots of any view or sheet; PDF, DWG and IFC export. |

<details><summary><b>Diagnosis</b> — see it</summary>

https://github.com/user-attachments/assets/55419990-a25a-4d80-a5e4-3ef0f78f13ce

<sub>Dutch: “I can't see the capstone in this 3D view any more. Why not? Put it back.” The chat reads the view's visibility gates, names the cause (hidden in view) and unhides it.</sub>

</details>

<details><summary><b>Information</b> — see it</summary>

https://github.com/user-attachments/assets/35ad70df-1da9-48c8-9dd9-456ba4dc13ac

<sub>Dutch: a point cloud over the model; “read the ground level along the new wall every 2 m” gives a table and a 3D view with the scan; “let the capstone top follow that” shape-edits the slab and opens a longitudinal section.</sub>

</details>

<details><summary><b>Modelling</b> — see it</summary>

https://github.com/user-attachments/assets/51adf70b-6ffc-46f9-8359-7a446694db9a

<sub>Dutch: “expansion joint every 10 m, 20 mm, with an elevation and a text label”; “replace the wooden piles by concrete piles 320x320, raked 1:5 outwards, numbered P01–P40”; “place a drowning-ladder recessed in the wall”. Each answer opens the view it made.</sub>

</details>

<details><summary><b>Sheets</b> — see it</summary>

https://github.com/user-attachments/assets/9c71a615-0444-4a4b-9e0d-dfff79a2a065

<sub>Dutch: “a pile schedule with number, type and rake”; “add the level marks to section 1-1”; “put section, elevation, the 3D of the piles and the schedule on a new A0 sheet, check that everything fits, export it as PDF”.</sub>

</details>

<details><summary><b>Context</b> — see it</summary>

https://github.com/user-attachments/assets/55be1a79-64a6-410a-b158-f4e6caf4d64c

<sub>Dutch: “which documents are in the project folder and what does the safety memo say?”; “remember: sections get phase 2 and the capstone is 200 mm”; “what was done in this model today?” — answered from the journal.</sub>

</details>

## How it works

<img src="docs/images/how-it-works.png" width="960" alt="The chat panel on the left; on the right the flow: you type a request, Claude reads the model state and picks commands, VibeModel's HTTP API inside Revit runs each one on the main thread in a transaction, and the model returns one answer, one undo step and one journal line.">

VibeModel runs a small HTTP server inside Revit on `127.0.0.1:18884`. The chat backend (Claude Code, the Anthropic API or a local model) calls it with plain commands; each command runs on Revit's main thread inside a transaction and returns text or JSON. The same API works from any terminal:

```bash
curl -s http://localhost:18884/health                       # is it running?
curl -s http://localhost:18884/help                         # every command
curl -s "http://localhost:18884/whyhidden?args=348122"      # why can't I see this one?
curl -s "http://localhost:18884/wall?args=0 0 5000 0 3000"  # a wall, in mm
```

Close to eighty commands cover reading, diagnosing, modelling, sheets, annotation, office standards, project documents, memory and export. The full reference, with every flag, is in [CLAUDE.md](CLAUDE.md).

## Your own standards

VibeModel does not know how your office models. You tell it with a **standards pack**: a folder of plain markdown and JSON that the person who owns your Revit standard maintains, no code involved.

1. **Copy the sample.** `standards/example-office/` is a complete, fictional pack: `pack.json` (phases, view templates, title block, library paths, link settings), `core.md` (the rules every chat turn gets), topic sheets (`topics/phasing.md`, `sheets.md`, …) and `checks.json` (what `check` audits).
2. **Fill in your office.** Your phase names, your view templates, where your family library lives, which parameters must be filled. Drop your modelling manual (PDF) next to it and run `standards --index` once: the chat can then quote it by chapter and cite it.
3. **Point VibeModel at it.** Settings → *Standards pack folder* → your office share. Every modeller gets the same pack without a new add-in release; the add-in mirrors it to the local machine, so a missing VPN only means yesterday's pack stays in force.

https://github.com/user-attachments/assets/01549e12-8f9f-47ff-ae17-dba4e2b72a42

<sub>Dutch, with a real office pack: “check this model against the office standard and colour the problems red”; “fill the BIM-ID fields and check again”; “put a temporary sheet-pile cofferdam 3 m behind the wall in the temporary-works phase”.</sub>

A pack only switches on for models that match its **fingerprint** (your phases and a shared parameter), so a stranger's model is never judged by your rules. And your pack stays yours: it lives on your share and your machines, never in this repository. The full format is in [standards/README.md](standards/README.md).

## Installation

### Requirements

- Autodesk Revit 2022 or newer (Windows)
- One way to reach Claude: a [Claude Code](https://docs.anthropic.com/en/docs/claude-code) subscription, an [Anthropic API key](https://console.anthropic.com/), or a local LLM server ([setup notes](docs/LOCAL_LLM_SETUP.md))

<details>
<summary><b>Option A — Claude Code (recommended)</b></summary>

1. Install Node.js, then `npm install -g @anthropic-ai/claude-code`
2. Run `claude` once in a terminal and log in
3. Start Revit. VibeModel detects Claude Code automatically.

This gives the chat file access to the project folder, cost tracking and the longest context.
</details>

<details>
<summary><b>Option B — Anthropic API key (pay-per-use)</b></summary>

1. Get a key from [console.anthropic.com](https://console.anthropic.com/)
2. In Revit, open the chat panel → **Settings** → paste the key

No Node.js needed. Some extras (project-folder file access, cost tracking) are Claude Code only.
</details>

<details>
<summary><b>Manual installation</b></summary>

1. Download and unzip the release zip (`VibeModel-v<version>-Revit2023.zip`). Copy `VibeModel.dll`, `VibeModel.addin`, `Markdig.dll` and the `VibeModel.PdfText` folder into
   ```
   %APPDATA%\Autodesk\Revit\Addins\{year}\
   ```
   with `{year}` your Revit version (`2023`, `2024`, …). Create the folder if it doesn't exist.
2. **Unblock the DLLs.** Windows blocks files downloaded from the internet. Right-click each `.dll` → *Properties* → *Unblock*, or in PowerShell:
   ```powershell
   Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\*\*.dll" -Recurse | Unblock-File
   ```
3. Restart Revit. A **VibeModel** tab appears in the ribbon.
</details>

<details>
<summary><b>Settings worth knowing</b></summary>

All in the chat panel's **Settings**:

- **Language** — the fallback reply language and the panel's own texts (English or Dutch).
- **Project folder** — where the model's documents live; defaults to the folder of the open `.rvt`.
- **Standards pack folder** — an office standards pack (phases, view templates, library, checks, manual). See [standards/README.md](standards/README.md) and the sample pack in `standards/example-office/`.
- **Project memory & journal** — on by default; writes `VibeModel\memory.md` and `VibeModel\journal.md` next to the project documents.
- Optional: set `VIBEMODEL_TOKEN` before starting Revit to require a token on every API call.
</details>

## Building from source

```powershell
git clone https://github.com/Quincy987/VibeModel.git
cd VibeModel
.\build.ps1                      # Revit 2023: closes Revit, builds, deploys, restarts
.\build.ps1 -RevitVersion 2024   # another version
dotnet test VibeModel.sln -p:RevitVersion=2023   # the unit tests, no Revit needed
```

Adding a command is one file: implement `IClaudeCommand` in `src/VibeModel/Services/Claude/Commands/` and build; it is discovered by reflection and shows up in `help`, in the chat's tool list and in the step labels. The developer reference is [CLAUDE.md](CLAUDE.md); design notes per feature are in [docs/plans](docs/plans/).

## Troubleshooting

<details>
<summary><b>The VibeModel tab doesn't appear</b></summary>

All files must be in the add-ins folder for *your* Revit year, and the DLLs must be unblocked (see manual installation). Then restart Revit. If Revit shows *External Tool Failure* / `0x80131515`, the DLLs are still blocked.

The most common cause: the downloaded zip was GitHub's *Source code (zip)*, which contains no compiled add-in. Download the `VibeModel-v<version>-Revit2023.zip` under *Assets* instead; `install.cmd` names this when it happens, and keeps its window open so the message can be read.
</details>

<details>
<summary><b>The chat says it is not connected</b></summary>

Claude Code: run `claude --version` in a terminal; if it fails, reinstall and log in. API key: open **Settings** and check the key. Logs are in `%LOCALAPPDATA%\VibeModel\logs\`.
</details>

<details>
<summary><b>Connection refused or the wrong port</b></summary>

Port 18884 is the default; a second Revit instance falls back to 18885–18888. Each live server writes its port to `%LOCALAPPDATA%\VibeModel\servers\<port>.json`. From scripts, use `127.0.0.1` rather than `localhost`. If no port is free at all, VibeModel falls back to a file-based mode (slower, still works).
</details>

<details>
<summary><b>Commands return errors</b></summary>

Open a project first (not the start screen), and check that the element you name still exists. Every error names the cause and, where there is one, the fix.
</details>

## License

[Mozilla Public License 2.0](https://www.mozilla.org/MPL/2.0/)
