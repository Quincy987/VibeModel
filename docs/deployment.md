# Deploying VibeModel in an office

For IT. VibeModel is a Revit add-in with an AI chat pane. It installs per Windows user and needs
**no administrator rights** at any step — install, connect, update, uninstall. This page covers
what goes where, how to pre-configure the connection to Claude so it "just works" when a modeller
starts Revit, what the network must allow, signing, and support.

Measured on Revit 2023 / Windows 11 (2026-10-14). The verification report with screenshots is
[verify/onboarding-2026-10-14.md](verify/onboarding-2026-10-14.md).

## What gets installed where

| Path | What |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<year>\VibeModel.Loader.dll`, `VibeModel.addin` | The **loader**, for the Revit year the zip is built for (`revit-year.txt`; Revit loads every `.addin` in this folder). It starts the installed version below and is the only file Revit's security prompt is about; updates never replace it |
| `%LOCALAPPDATA%\VibeModel\versions\Revit<year>\<version>\` | The add-in itself, one folder per installed version: `VibeModel.dll`, `Markdig.dll` and the out-of-process PDF text helper `VibeModel.PdfText\`. An in-app update unpacks the whole release zip here |
| `%LOCALAPPDATA%\VibeModel\versions\Revit<year>\current.txt` | One line: the version the loader starts next time (written by the installer, an update and a rollback) |
| `%LOCALAPPDATA%\VibeModel\updates\` | A download in progress and the last update manifest (so the banner still shows after a restart) |
| `%LOCALAPPDATA%\VibeModel\settings.json` | The user's settings; an API key in it is DPAPI-encrypted (`AnthropicApiKeyProtected`) |
| `%LOCALAPPDATA%\VibeModel\logs\` | `log_<date>.txt` (warnings/errors always; everything with `VIBEMODEL_DEBUG=1`), `install.log`, `loader.log` (one line per Revit start: which version, from where, why), saved Diagnose reports |
| `%LOCALAPPDATA%\VibeModel\chats\`, `servers\`, … | Chat history, the live HTTP server's port file |
| `%ProgramData%\VibeModel\managed.json` *(optional, yours)* | Office-wide settings, read-only for VibeModel — see below |
| `%USERPROFILE%\.local\bin\claude.exe` *(optional)* | Claude Code, only if a user installs it |

The add-in runs a small HTTP server on `127.0.0.1:18884` (18885–18888 for a second Revit), loopback
only, and **token-gated**: every call except `/health` must carry the `X-VibeModel-Token` header.
Each Revit session makes a random token and writes it to the user's own
`%LOCALAPPDATA%\VibeModel\servers\<port>.json`; the chat pane and the Claude Code CLI it starts use it
automatically, nothing to configure. Set `VIBEMODEL_TOKEN` to choose the token yourself (it wins over
the generated one); `"OpenServer": true` in `managed.json` restores the open server for scripts that
cannot read the file (not recommended: any local process can then run `exec`). Requests made by a web
browser (an `Origin` or `Sec-Fetch-*` header, a `Host` other than 127.0.0.1/localhost) are refused
with 403 whatever the token mode. See [../SECURITY.md](../SECURITY.md).

## Installing

The release zip `VibeModel-v<version>-Revit<year>.zip` contains `install.cmd` / `install.ps1`.

```bat
install.cmd            :: interactive: asks only to close Revit, keeps its window open
install.cmd -Quiet     :: no questions, no pause - for Intune / login scripts
install.cmd -InstallClaudeCode   :: also install Claude Code (optional, see below)
install.cmd -AllowDowngrade      :: start this version even when a newer one is installed
```

`-Quiet` exit codes: `0` installed or already up to date, `1` failed (see `install.log`),
`2` Revit is running and holds a file that has to change — the loader or the `.addin` (they change
only when the loader itself changes), or an add-in an install from before 1.2.14 left in the add-ins
folder — retry later, `3` the Revit this build is for is
not installed for this user. A zip is built for one Revit year (`VibeModel-v<version>-Revit2023.zip`
→ `revit-year.txt` = 2023): the installer installs only there and names other Revit versions it
leaves alone — a build only loads in the Revit it was compiled against.
The installer is idempotent: unchanged files are reported as "already up to date". It removes the
"downloaded from the internet" mark (`Unblock-File`) and verifies every file afterwards. A new version
of the add-in alone installs **while Revit runs** (it goes into its own version folder) and is active
after Revit restarts — the installer says so. It removes a `VibeModel.dll`, `Markdig.dll`,
`VibeModel.pdb` or `VibeModel.PdfText\` that an older install left in the add-ins folder: the loader
would start that one instead of the installed version (its rule for a developer build). It never
downgrades silently: when a newer version is already installed (by the in-app update or an earlier
zip), an older zip is installed next to it and the newer one stays active — `-AllowDowngrade` starts
the older one on purpose.

**Intune / login script (per user):** run the zip's `install.cmd -Quiet` in the user's context
(not SYSTEM — `%APPDATA%` must be the user's). Re-run it with a newer zip to update — or let users
update in the app (next section). A login script that keeps running the same zip does no harm: it
reports "already up to date" and never undoes a newer in-app update.
Uninstall = delete the files listed in the table.

## Updates

Since 1.2.14 the chat pane updates VibeModel itself (an install from before 1.2.14 has no loader yet:
run that release's `install.cmd` once; every later version can come through the app). Once a day (8 s after Revit starts, in the
background) it reads the update manifest; when a newer version is published, a slim banner under the
chat header says **VibeModel 1.2.15 beschikbaar (2,6 MB)** with *Bijwerken* (Update), *Wat is nieuw*
(the release page) and *Niet nu*. *Bijwerken* downloads the release zip, checks its size and SHA-256
against the manifest, unpacks it into `versions\Revit<year>\<version>\` and points `current.txt` at it.
**It is active after the next Revit start** — Revit is never restarted for the user, the running
version is never touched and nothing changes in the model. The loader stays the same file, so Revit
does **not** ask *Always Load* again (measured with unsigned test builds: an update to a new add-in
version started without a prompt). The running version, the new one and one older version are kept; Settings → *Versie en
updates* offers *Nu controleren* and *Terug naar &lt;version&gt;* (a rollback, at the next start). In
the chat or a terminal: `update`, `update --install`, `update --rollback`.

A download that does not match the manifest is deleted and nothing is installed ("the download did
not match the published checksum"); a zip entry that would land outside its version folder is refused.
When a version needs a newer loader (the manifest's `minLoader`), the banner says to run `install.cmd`
from that zip once instead.

**Where updates come from.** By default the newest published GitHub release of
`Quincy987/VibeModel`: `https://github.com/Quincy987/VibeModel/releases/latest/download/latest-Revit<year>.json`
(drafts and pre-releases are invisible; when the latest release has no manifest, the GitHub API's
release list is read for the newest `v…` release that has one). Each release carries three assets,
made by `scripts/package-release.ps1` and uploaded by the release workflow:

```
VibeModel-v1.2.14-Revit2023.zip           the add-in
VibeModel-v1.2.14-Revit2023.zip.sha256    "<sha256>  VibeModel-v1.2.14-Revit2023.zip"
latest-Revit2023.json                     the manifest:
{"version":"1.2.14","revitYear":"2023","zip":"VibeModel-v1.2.14-Revit2023.zip",
 "sha256":"<64 hex>","size":2724574,"published":"2026-10-20",
 "notes":"https://github.com/Quincy987/VibeModel/releases/tag/v1.2.14","minLoader":"1.0.0"}
```

**Your own mirror.** Copy a release's `latest-Revit<year>.json` and its zip into one folder — a share
(`\\server\apps\VibeModel`) or an https location — and set `UpdateSource` in `managed.json` to it (or
to the manifest file itself). Laptops then update from there only, when you put a new version there; a
share that does not answer within 3 s is reported as a failed check, never waited on. Keep the zip's
release name and do not edit it: the manifest's checksum is what is checked.

| managed.json | Effect |
|---|---|
| `"DisableUpdates": true` | No check, no banner, no install; `update` answers "switched off by IT"; locked in Settings. For offices that roll out with Intune only. |
| `"UpdateSource": "\\\\server\\apps\\VibeModel"` | Check and download there instead of GitHub (an https URL or a UNC/local folder). |

Users can switch the automatic check off in Settings (*Automatisch controleren op updates*); a manual
check still works. Diagnose shows the loader, the folder VibeModel runs from, `current.txt`, the
installed versions, the source, and the last check and its result.

## Connecting to Claude

VibeModel can talk to Claude three ways. In the default `auto` mode it takes the first that works:

1. **An API key (recommended for an office).** The built-in connection goes straight from Revit to
   the Anthropic Messages API — nothing else to install. It uses the **Windows proxy settings and the
   user's Windows credentials** (NTLM/Kerberos proxies work) and the **Windows certificate store**
   (a TLS-inspecting proxy whose root you deploy just works; validation is never relaxed).
2. **Claude Code** (a user's own Claude subscription). Optional. Installed per user by Anthropic's
   installer (`irm https://claude.ai/install.ps1 | iex`, no admin) — from the chat pane with one click,
   or `install.cmd -InstallClaudeCode`. Needs an interactive login once. Note: Claude Code supports
   proxies through `HTTPS_PROXY` with basic credentials only, **not NTLM/Kerberos**; VibeModel hands it
   the Windows proxy address (`HTTPS_PROXY`, and `NO_PROXY` for 127.0.0.1/localhost) when none is
   set. Git for Windows is not required.
3. A local LLM server (llama.cpp, Ollama, LM Studio) — Settings → Local LLM.

When nothing is configured, the chat pane shows **Connect to Claude**: the user pastes a key (checked
against the API right away — free, no tokens — and stored DPAPI-encrypted only when accepted), or
installs / logs in to Claude Code.

### Pre-configuring it: `managed.json`

Put a `managed.json` in `%ProgramData%\VibeModel\` (machine) or `%APPDATA%\VibeModel\` (user). The first
one found wins; its keys override the user's settings and show as locked ("beheerd door IT") in
Settings. VibeModel never writes it. Changes are read within a minute; which chat connection is
used (`PreferredBackend`, a new key) is decided when Revit starts.

**The machine-wide file must be created by IT, as an administrator.** Windows lets ordinary users
create folders under `%ProgramData%`, so a user could otherwise plant a `VibeModel\managed.json`
that points everyone's chat at an endpoint or key of their choosing and looks like policy. VibeModel
therefore only trusts `%ProgramData%\VibeModel\managed.json` when **both the file and its folder are
owned by Administrators or SYSTEM** and give **no write access to Users, Authenticated Users,
Everyone or Interactive** (the inherited ProgramData ACL does — remove it). Anything else is ignored,
logged once as a warning and shown in Diagnose as `IGNORED <path>: <reason>`. Created from an elevated
PowerShell, this ACL passes:

```powershell
$dir = "$env:ProgramData\VibeModel"
New-Item -ItemType Directory -Force $dir | Out-Null
icacls $dir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX"
Set-Content -Encoding utf8 "$dir\managed.json" '{ "PreferredBackend": "anthropic-api", "AnthropicApiKey": "sk-ant-..." }'
icacls "$dir\managed.json" /setowner "*S-1-5-32-544"
```
(`S-1-5-32-544` = Administrators, `S-1-5-18` = SYSTEM, `S-1-5-32-545` = Users — SIDs, so the
commands work on a Dutch or English Windows alike. `/inheritance:r` drops the inherited Users
create-rights; a folder created by Intune/SCCM as SYSTEM already has the right owner.)

```json
{
  "PreferredBackend": "anthropic-api",
  "AnthropicApiKey": "sk-ant-api03-...",
  "Model": "claude-sonnet-5-5",
  "ApiBaseUrl": "https://api.anthropic.com",
  "DisableClaudeCode": true,
  "Language": "nl"
}
```

| Key | Meaning |
|---|---|
| `PreferredBackend` | `auto` (default), `anthropic-api`, `claude-cli`, `local-llm` |
| `AnthropicApiKey` | The key everyone uses. Plain text in this file — protect the file with an ACL (users: read). Alternatively set the `ANTHROPIC_API_KEY` environment variable. |
| `Model` | Model id for the API connection (default `claude-sonnet-5-5`) |
| `ApiBaseUrl` | Another endpoint that speaks the Anthropic Messages API with an `x-api-key` header (a company gateway). `/v1/messages` is appended. Claude on Microsoft Foundry has not been tested with this. |
| `DisableClaudeCode` | `true` hides the Claude Code option entirely |
| `ClaudeCliPath` | Full path to `claude.exe` if it lives elsewhere; `none` switches discovery off |
| `Language` | `nl` or `en`: the pane's texts and the fallback reply language |
| `OpenServer` | `true` runs the local HTTP server **without** a token, as before 1.2.11 (default `false`). Only for scripts that cannot read the token from `%LOCALAPPDATA%\VibeModel\servers\<port>.json`; a set `VIBEMODEL_TOKEN` still wins. |
| `ChangeApproval` | What happens to the AI's deletions and edits: `auto` (default, nothing changes), `ask` (the user keeps or discards each one in a window in Revit), `readonly` (every change is refused — for people who only look at models). Locked in Settings when set here. |
| `DisableUpdates` | `true` switches the in-app update off entirely (see *Updates*) |
| `UpdateSource` | Where updates come from: an https URL or a UNC/local folder holding `latest-Revit<year>.json` and the zip (default: GitHub releases) |

Key precedence: `managed.json` → `ANTHROPIC_API_KEY` → the user's own (encrypted) key.
A key from `managed.json` or the environment is never copied into the user's settings.

**Budget:** create one API key per team in the Anthropic Console and set a spend limit on it. Every
answer in the chat shows its tokens and an estimated cost (`31.0k tokens · ≈ €0,12`, list prices).

### Environment variables

| Variable | Effect |
|---|---|
| `ANTHROPIC_API_KEY` | API key (after managed.json) |
| `VIBEMODEL_CLAUDE_CLI` | Path to `claude.exe`, or `none` |
| `VIBEMODEL_TOKEN` | Use this token on the local HTTP server instead of the per-session one (must be set in the environment Revit starts from) |
| `VIBEMODEL_DEBUG=1` | Log everything (INFO too) |
| `HTTPS_PROXY` / `HTTP_PROXY` | Used by Claude Code; the API connection uses the Windows proxy settings |

## Network

The API connection needs only **`api.anthropic.com`** (HTTPS 443).

Claude Code additionally needs `claude.ai`, `claude.com`, `platform.claude.com` (login),
`downloads.claude.ai` (installer and auto-updates) — the full list, including optional telemetry
hosts, is Anthropic's [network requirements](https://code.claude.com/docs/en/network-config#network-access-requirements).
Claude Code updates itself; to pin a version, set `DISABLE_AUTOUPDATER` in Claude Code's
managed settings ([setup](https://code.claude.com/docs/en/setup#disable-auto-updates)).

In-app updates (unless `DisableUpdates` or your own `UpdateSource`): `github.com` and the release
download hosts it redirects to (`objects.githubusercontent.com`, `release-assets.githubusercontent.com`),
plus `api.github.com` only when the latest release has no manifest. Same Windows proxy and certificate
store as the API connection.

TLS inspection: the API connection uses the Windows store. Claude Code's native build trusts the
Windows store too (`bundled,system` by default); otherwise point `NODE_EXTRA_CA_CERTS` at the root PEM.

## Signing

Revit checks the Authenticode signature of the assembly the `.addin` names — since 1.2.14 the
**loader** (`VibeModel.Loader.dll`), which in-app updates never replace. Measured in Revit 2023:

| The DLL is… | Revit shows |
|---|---|
| **signed with a certificate this computer trusts** | nothing — it loads |
| unsigned (today's release) | "Security - Unsigned Add-In" **once** → *Always Load*; again only when `install.cmd` installs a changed loader. Revit stores the answer when it closes normally — a Revit that is killed asks again |
| signed, but the certificate is not trusted (e.g. self-signed) | "Security - Invalid Signature … may have been tampered with" on **every** start, only *Load* / *Do Not Load* |

So: either ship unsigned (users click *Always Load* once), or sign with a code-signing
certificate whose chain your laptops trust — your internal CA, deployed to *Trusted Root* and
*Trusted Publishers* by GPO/Intune — and nobody sees a dialog. Never ship a self-signed build.

```powershell
.\scripts\package-release.ps1 -Sign -Thumbprint <code-signing cert thumbprint>   # or -PfxPath
```

`scripts\sign.ps1` uses `Set-AuthenticodeSignature` with a timestamp (no Windows SDK needed) and signs
`VibeModel.Loader.dll`, `VibeModel.dll`, `VibeModel.PdfText.exe` and `install.ps1`. AppLocker / WDAC:
allow `%LOCALAPPDATA%\VibeModel\versions\*\*\VibeModel.PdfText\VibeModel.PdfText.exe` (and
`%USERPROFILE%\.local\bin\claude.exe` if Claude Code is used) — a publisher rule is easiest once signed.

## What leaves the laptop

Only when a user asks the chat something, and only to the configured endpoint (api.anthropic.com
by default): the question, the chat so far, a short description of the open model (title, active
view, selection), the results of the Revit commands Claude runs to answer (element data, parameter
values), a view screenshot when Claude takes one, and document text when the user asks about a
project document. The office standards pack's rules and project memory notes go along as context.
After each answer one small extra call (the last few messages, cut short) asks for the follow-up
buttons shown under it. Nothing else is sent; there is no telemetry in VibeModel itself (Claude Code,
when used, has its own — see Anthropic's network page). The API key leaves the machine only in the
request header.

## Support

- **Diagnose** (button in the chat header, or `curl http://127.0.0.1:18884/diagnose`): version,
  signature, which connection is in use, where the key comes from (masked), where Claude Code was
  looked for, the proxy, whether `api.anthropic.com` and `claude.ai` answer and who issued their
  certificates (reveals TLS inspection), this session's warnings and errors, and the settings with
  secrets masked. Users copy it or save it to `%LOCALAPPDATA%\VibeModel\logs\diagnose-<time>.txt`.
- `%LOCALAPPDATA%\VibeModel\logs\install.log` — every installer run.
- `%LOCALAPPDATA%\VibeModel\logs\loader.log` — every Revit start: the loader version, which
  `VibeModel.dll` it started and why (`current.txt`, the newest version folder, or a developer build
  next to the loader).
- `%LOCALAPPDATA%\VibeModel\logs\log_<date>.txt` — warnings and errors.
