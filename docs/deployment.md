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
| `%APPDATA%\Autodesk\Revit\Addins\<year>\VibeModel.dll`, `VibeModel.addin`, `Markdig.dll` | The add-in, for the Revit year the zip is built for (`revit-year.txt`; Revit loads every `.addin` in this folder) |
| `%APPDATA%\Autodesk\Revit\Addins\<year>\VibeModel.PdfText\` | Out-of-process PDF text helper (`VibeModel.PdfText.exe`) |
| `%LOCALAPPDATA%\VibeModel\settings.json` | The user's settings; an API key in it is DPAPI-encrypted (`AnthropicApiKeyProtected`) |
| `%LOCALAPPDATA%\VibeModel\logs\` | `log_<date>.txt` (warnings/errors always; everything with `VIBEMODEL_DEBUG=1`), `install.log`, saved Diagnose reports |
| `%LOCALAPPDATA%\VibeModel\chats\`, `servers\`, … | Chat history, the live HTTP server's port file |
| `%ProgramData%\VibeModel\managed.json` *(optional, yours)* | Office-wide settings, read-only for VibeModel — see below |
| `%USERPROFILE%\.local\bin\claude.exe` *(optional)* | Claude Code, only if a user installs it |

The add-in runs a small HTTP server on `127.0.0.1:18884` (18885–18888 for a second Revit), loopback
only. Set `VIBEMODEL_TOKEN` to require a token on it.

## Installing

The release zip `VibeModel-v<version>-Revit<year>.zip` contains `install.cmd` / `install.ps1`.

```bat
install.cmd            :: interactive: asks only to close Revit, keeps its window open
install.cmd -Quiet     :: no questions, no pause - for Intune / login scripts
install.cmd -InstallClaudeCode   :: also install Claude Code (optional, see below)
```

`-Quiet` exit codes: `0` installed or already up to date, `1` failed (see `install.log`),
`2` Revit is running (the DLL would be locked — retry later), `3` the Revit this build is for is
not installed for this user. A zip is built for one Revit year (`VibeModel-v<version>-Revit2023.zip`
→ `revit-year.txt` = 2023): the installer installs only there and names other Revit versions it
leaves alone — a build only loads in the Revit it was compiled against.
The installer is idempotent: unchanged files are reported as "already up to date". It removes the
"downloaded from the internet" mark (`Unblock-File`) and verifies every file afterwards.

**Intune / login script (per user):** run the zip's `install.cmd -Quiet` in the user's context
(not SYSTEM — `%APPDATA%` must be the user's). Re-run it with a newer zip to update.
Uninstall = delete the files listed in the table.

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

Key precedence: `managed.json` → `ANTHROPIC_API_KEY` → the user's own (encrypted) key.
A key from `managed.json` or the environment is never copied into the user's settings.

**Budget:** create one API key per team in the Anthropic Console and set a spend limit on it. Every
answer in the chat shows its tokens and an estimated cost (`31.0k tokens · ≈ €0,12`, list prices).

### Environment variables

| Variable | Effect |
|---|---|
| `ANTHROPIC_API_KEY` | API key (after managed.json) |
| `VIBEMODEL_CLAUDE_CLI` | Path to `claude.exe`, or `none` |
| `VIBEMODEL_TOKEN` | Require this token on the local HTTP server |
| `VIBEMODEL_DEBUG=1` | Log everything (INFO too) |
| `HTTPS_PROXY` / `HTTP_PROXY` | Used by Claude Code; the API connection uses the Windows proxy settings |

## Network

The API connection needs only **`api.anthropic.com`** (HTTPS 443).

Claude Code additionally needs `claude.ai`, `claude.com`, `platform.claude.com` (login),
`downloads.claude.ai` (installer and auto-updates) — the full list, including optional telemetry
hosts, is Anthropic's [network requirements](https://code.claude.com/docs/en/network-config#network-access-requirements).
Claude Code updates itself; to pin a version, set `DISABLE_AUTOUPDATER` in Claude Code's
managed settings ([setup](https://code.claude.com/docs/en/setup#disable-auto-updates)).

TLS inspection: the API connection uses the Windows store. Claude Code's native build trusts the
Windows store too (`bundled,system` by default); otherwise point `NODE_EXTRA_CA_CERTS` at the root PEM.

## Signing

Revit checks the add-in's Authenticode signature at start. Measured in Revit 2023:

| The DLL is… | Revit shows |
|---|---|
| **signed with a certificate this computer trusts** | nothing — it loads |
| unsigned (today's release) | "Security - Unsigned Add-In" **once per new version** → *Always Load* |
| signed, but the certificate is not trusted (e.g. self-signed) | "Security - Invalid Signature … may have been tampered with" on **every** start, only *Load* / *Do Not Load* |

So: either ship unsigned (users click *Always Load* once per update), or sign with a code-signing
certificate whose chain your laptops trust — your internal CA, deployed to *Trusted Root* and
*Trusted Publishers* by GPO/Intune — and nobody sees a dialog. Never ship a self-signed build.

```powershell
.\scripts\package-release.ps1 -Sign -Thumbprint <code-signing cert thumbprint>   # or -PfxPath
```

`scripts\sign.ps1` uses `Set-AuthenticodeSignature` with a timestamp (no Windows SDK needed) and signs
`VibeModel.dll`, `VibeModel.PdfText.exe` and `install.ps1`. AppLocker / WDAC: allow
`%APPDATA%\Autodesk\Revit\Addins\*\VibeModel.PdfText\VibeModel.PdfText.exe` (and
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
- `%LOCALAPPDATA%\VibeModel\logs\log_<date>.txt` — warnings and errors.
