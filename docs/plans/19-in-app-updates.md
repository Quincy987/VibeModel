# Plan 19 — In-app updates: a stable loader and an Update button

> Goal: a modeller sees "VibeModel 1.2.15 beschikbaar" in the chat pane, clicks **Bijwerken**, and
> after the next Revit start runs the new version — no download page, no installer, no new "Always
> Load" prompt, no administrator rights. IT can switch it off or point it at its own share. The
> previous version is kept for a rollback.

> **STATUS (built 2026-10-20, branch claude-bot/t-371).** Loader project, update service, banner,
> Settings section, Diagnose section, `update` command, installer + packaging + release workflow,
> docs. Headless tests: `LoaderPathsTests`, `UpdatePureTests`, `UpdateEngineTests` (1585 green after
> merging v1.2.13). Live verification in Revit 2023: see the end. v1.2.13 shipped without this, so the
> first release with the loader is 1.2.14 — installed with `install.cmd` once; the version numbers in
> "Measured" are test builds made from this branch.

## Before

Users downloaded the release zip, closed Revit and ran `install.cmd` again; nothing in the add-in
knew about newer versions. And because the DLL the `.addin` names changed with every version, an
unsigned build made Revit ask *Always Load* after every update.

## The constraint, and the design

Revit locks the add-in DLL while it runs, and .NET Framework cannot unload an assembly — an update can
never replace the running file, and never takes effect before Revit restarts. So:

1. **A stable loader is what Revit starts.** `VibeModel.addin` names `VibeModel.Loader.dll`
   (`src/VibeModel.Loader/`, RevitAPI + RevitAPIUI only, its own version 1.0.0, the same ClientId so
   Revit sees one add-in). It picks the payload with `LoaderPaths.Choose` (pure; the same source file is
   compiled into VibeModel.dll, so the add-in reports and changes what the loader will start):
   (a) `VibeModel.dll` next to the loader — a developer build (`build.ps1` deploys it there) wins;
   (b) the folder `versions\Revit<year>\current.txt` names; (c) the newest complete version folder
   (by version, not by name; `.tmp` and other names are not versions); (d) nothing → one TaskDialog
   listing where it looked, `Result.Failed`, Revit runs on.
   It loads the payload with `Assembly.LoadFrom` (Markdig resolves from the same folder; an
   `AssemblyResolve` fallback probes that folder by simple name), sets `Services.Update.LoaderInfo`
   (`Version`, `Path`, `Rule`) by reflection — a name contract, no reference either way — and passes
   OnStartup/OnShutdown on. One line per start in `logs\loader.log`; a payload that throws in OnStartup
   gets a dialog naming its folder and the log.
2. **Every version has its own folder**: `%LOCALAPPDATA%\VibeModel\versions\Revit<year>\<version>\`.
   *Per Revit year* — a decision made while building: the 2023 and 2024 builds of one version are
   different files (each compiled against its own RevitAPI), so `versions\<version>` alone would let a
   2024 install overwrite the folder a 2023 Revit runs from. `current.txt` sits next to the folders.
3. **An update only adds a folder and rewrites current.txt.** The running folder and the loader are
   never touched. The loader inside the zip is unpacked with the rest but never copied over the
   installed one (it is locked, and it is the file Revit's prompt is about); when the zip carries a
   newer loader the result says "the loader changed too — run install.cmd from … once".

## The update service (`src/VibeModel/Services/Update/`)

- Pure, unit-tested: `UpdateManifest` (parse + validate: version, Revit year, zip name exactly
  `VibeModel-v<version>-Revit<year>.zip`, sha256 64 hex, size > 0, `minLoader`, https-only notes link),
  `UpdateSource` (default GitHub `releases/latest/download/latest-Revit<year>.json`; an https URL or a
  UNC/local folder or manifest; `http://` and relative paths refused; `ZipUri` takes GitHub's zip from
  the manifest's own tag `releases/download/v<version>/`, so a release published between the two
  downloads cannot swap the zip; `PickFromReleases` for the API fallback), `UpdateDecision` (states in
  order: Disabled, InPlaceBuild, ReadyAtRestart, Failed/NotChecked, UpToDate, LoaderTooOld, Available;
  versions compared normalized — 1.2.13 == 1.2.13.0), `UpdateLayout` (paths, current.txt written
  atomically, `RollbackTarget`, `Prune`) and `ZipSafety` (no `..`, no rooted path, no drive letter; one
  bad entry refuses the whole zip before anything is written).
- `UpdateEngine` — the I/O, with `IUpdateTransport` (https through `AnthropicHttp.CreateHandler()`:
  Windows proxy with the user's credentials, Windows certificate store; file:// with a 3 s deadline on
  the manifest) and `IUpdateSettings`, so tests run against a temp folder and a local "mirror".
  - `Check(manual)`: automatic checks are skipped when Settings says so or the last one was < 24 h ago
    — then the manifest cached in `%LOCALAPPDATA%\VibeModel\updates\manifest-Revit<year>.json` (with
    the source it came from) still decides, so the banner shows after every restart. GitHub's latest
    release without a manifest (404) → the releases API → the first published `v…` release that has one.
  - `Install()`: download to `updates\<zip>.partial` (10 min deadline; a stalled connection is closed
    by the cancellation), size + SHA-256 must match ("the download did not match the published
    checksum … nothing was installed"), unpack into `<version>.tmp`, check `VibeModel.dll` is at its top,
    rename to `<version>`, write current.txt, prune (keep the running, the current and one older
    version; a folder another Revit still uses cannot be renamed and is left alone), delete the zip. An
    already complete folder skips the download; a failed one leaves no zip, `.partial` or `.tmp` behind.
  - Three kinds of failure, worded in one place (`Failure`): a failed **install** keeps the update
    *Available* with its `Reason` (the banner says *Bijwerken mislukt*), also after a later check, until
    an install succeeds; a failed **check** is `Failed` only when no manifest is known — with a cached
    one that still decides, and the failure is `LastError` (review round 1: an offline check after 24 h
    otherwise showed "Bijwerken mislukt" though nobody pressed Bijwerken); any other failed **action**
    (rollback, cancel) is `LastError` too. Settings, `update` and Diagnose show `LastError` on its own line.
  - `Pending` (current.txt ≠ the running folder) only counts a complete folder — for anything else the
    loader falls back to the newest version, so "next start: X" would be untrue.
  - One English wording of the state, `UpdateDecision.Describe` (`update`, Diagnose, the log,
    UpdateLastResult); the pane's localized one is `UiText.UpdateLine`.
  - `Rollback()`: current.txt → the newest installed version older than the one Revit starts next.
- `UpdateService`: the session's engine (layout for `RevitYear`, set in OnStartup from Revit), the
  startup check 8 s after OnStartup on a worker thread, `CheckAsync` / `InstallAsync` /
  `RollbackAsync`, `Changed` for the UI.

## Where the user meets it

- **Banner** (`UI/UpdateBanner.cs`, its own grid row under the chat header, so messages never shift):
  Available → *VibeModel 1.2.14 beschikbaar (2,6 MB)* + **Bijwerken** / *Wat is nieuw* / *Niet nu*;
  Installing → *Downloaden… 1,0 MB / 2,6 MB*, *Uitpakken…*; ReadyAtRestart → *VibeModel 1.2.14 is klaar
  - actief na het opnieuw starten van Revit.* (a rollback: *Revit start de volgende keer VibeModel
  1.2.11.*) + ×; LoaderTooOld → *… eenmalig installeren met install.cmd uit de zip.* + *Downloaden*
  (the release page); a failed install → *Bijwerken mislukt: <reason> Klik op Diagnose…* + *Opnieuw*.
  Never shown for a failed automatic check. *Niet nu* hides it for the session; an install started
  anywhere brings it back. Texts: `Strings` (nl/en), composed by `UiText.UpdateLine`.
- **Settings → Versie en updates**: installed version + loader, runs from (or "a developer build …"),
  update source (+ "beheerd door IT"), *Automatisch controleren op updates*, *Nu controleren* + result
  line, the pending version, *Terug naar <version>*.
- **Diagnose** `== Updates`: loader + rule, running from, current.txt, versions installed (running
  marked), source, automatic check / DisableUpdates, last check + result, state now.
- **`update`** (no document needed; not a model change — no transaction, undo step, journal or
  approval): `update` checks now; `update --install` checks, then installs in the background and
  returns at once; `update --rollback`. JSON `data`: state, installed, loader, runningFrom, inPlace,
  source, latest, pending, reason, note, rollbackTo, checkedAt, progress. StatusVerbs in both maps.

## Settings and IT

- managed.json: `DisableUpdates` (no check, banner, install; `update` says so; Settings locked),
  `UpdateSource` (https URL or UNC/local folder; also read from settings.json for a test mirror).
- settings.json: `UpdateCheck` (default true), `UpdateLastChecked` (UTC ISO), `UpdateLastResult`.
- Packaging: `package-release.ps1` adds the loader to the zip and writes `<zip>.sha256` and
  `latest-Revit<year>.json` (`minLoader` 1.0.0); `release.yml` uploads all three; `sign.ps1` signs the
  loader too. `install.ps1`: loader + .addin → add-ins folder, payload → its version folder,
  current.txt, removes the pre-loader files from the add-ins folder; Revit has to be closed only when
  the loader/.addin change or old files must go (`-Quiet` exit 2 only then).
- Docs: deployment.md (*Updates*, the table, managed keys, network hosts, signing), README (Quick start,
  manual install, troubleshooting), CLAUDE.md, CHANGELOG.

## Measured (Revit 2023, 2026-10-20, this machine's live add-ins folder backed up and restored)

- **install.cmd -Quiet** from the packaged 1.2.12 zip: loader + .addin in `Addins\2023`, the old
  `VibeModel.dll`, `Markdig.dll`, `VibeModel.PdfText\` removed from it, `versions\Revit2023\1.2.12\`
  complete, `current.txt` = 1.2.12, exit 0.
- **First start**: "Security - Unsigned Add-In" once (a new loader — expected), then `loader.log`:
  `loader 1.0.0.0 starts VibeModel 1.2.12.0 from …\versions\Revit2023\1.2.12\VibeModel.dll
  (current.txt)`; `/health` OK.
- **Check** against a local mirror holding a 1.2.13 build: `update` → *VibeModel 1.2.13 is
  available*; the banner read *VibeModel 1.2.13 beschikbaar (2,6 MB)* with Bijwerken / Wat is nieuw /
  Niet nu (UIA + window capture).
- **Install** by invoking Bijwerken: about 12 s later *VibeModel 1.2.13 is klaar - actief na het
  opnieuw starten van Revit.*; `versions\Revit2023\1.2.13\` complete, `current.txt` = 1.2.13, no zip left.
- **Restart**: loader started 1.2.13 (current.txt); `update` → up to date, rollback possible to 1.2.12.
  This start showed the security prompt again — because the previous Revit had been **killed** after
  *Always Load*: Revit stores the answer when it closes normally. Closed normally and started again
  with nothing changed: no prompt. Then **1.2.13 → 1.2.14** through `update --install`, Revit closed
  normally and started: **no prompt**, loader started 1.2.14. So the prompt is about the loader only;
  a new add-in version behind it does not bring it back.
- **Rollback**: `update --rollback` → *Rollback: Revit starts VibeModel 1.2.13 next time*, current.txt
  = 1.2.13 (a newer release is then not offered until the restart).
- **Tamper**: a 1.2.15 manifest with a wrong sha256 → `the download did not match the published
  checksum (… expected … 000000000000...) - nothing was installed`; no 1.2.15 folder, no partial file,
  current.txt unchanged.
- **DisableUpdates** in `%APPDATA%\VibeModel\managed.json` (read within a minute): `update` and
  `update --install` → *Updates are switched off by IT* (since review round 1 the engine words it like
  every other state: `State: updates are switched off by IT (managed.json DisableUpdates)`).
- **Installer downgrade guard** (review round 1, a sandbox with its own APPDATA/LOCALAPPDATA): 1.2.14
  installed → the 1.2.12 zip: *VibeModel 1.2.14 (newer, installed earlier) stays active; 1.2.12 is
  installed next to it*, current.txt stays 1.2.14 → the same zip with `-AllowDowngrade`: current.txt
  = 1.2.12. Before, a login script re-running an older zip would have undone every in-app update.
- **Diagnose** `== Updates` with a model open: loader 1.0.0 by rule Current, running from
  `…\1.2.14\VibeModel.dll`, current.txt 1.2.14, versions 1.2.14 (running), 1.2.13, 1.2.12, source,
  last check, state.
- **Ribbon Chat button** with the add-in in a version folder: pane hidden through `exec`, the ribbon's
  *Chat* invoked → the pane is back (the button's assembly path is the payload's own location).

## Not done / later

- No signed build yet: with an office certificate the one remaining prompt (a new loader) disappears too.
- No progress for an install started from the `update` command other than polling `update`.
- Revit 2024/2022 builds use the same code; only 2023 is packaged by the release workflow today.
