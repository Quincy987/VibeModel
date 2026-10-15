# Security

VibeModel puts an HTTP server inside Revit so that an AI assistant can read and change the open
model. That is the feature, and also the attack surface. This page says what the add-in does to
keep that surface to the user's own tools, and how to report a problem.

## What the server is

- **Loopback only.** The server binds `127.0.0.1` (IPv4 loopback) on port 18884 (18885–18888 when a
  second Revit runs). Nothing on the network can reach it.
- **Runs as the user.** Every command runs inside Revit, with the user's rights. `exec` compiles and
  runs arbitrary C#; `readdoc`, `docs`, `memory` and `journal` read files from the project folder.
  Whoever can call the server can do anything the user can do at that PC.
- **Token-gated by default** (since 1.2.11). Each Revit session generates a random 256-bit token and
  writes it to the user's own `%LOCALAPPDATA%\VibeModel\servers\<port>.json` — readable by that
  Windows user and administrators, the same people who could read Revit's memory. Every request except
  `/health` must send it in `X-VibeModel-Token`; the chat pane and the Claude Code CLI it starts get
  it automatically. `VIBEMODEL_TOKEN` in Revit's environment replaces the generated token.
  `"OpenServer": true` (settings.json or IT's managed.json) switches the token off — the pre-1.2.11
  behaviour, for scripts that cannot read the file. Don't, unless you know every process on the PC.
- **Browser requests are refused** (403), token or not. A web page can reach loopback: an
  `<img src="http://127.0.0.1:18884/…">` is a GET no browser blocks, and a domain that resolves to
  127.0.0.1 (DNS rebinding) is even same-origin and could read answers. Browsers always mark such
  requests, so the server refuses any request with an `Origin` header, a `Sec-Fetch-Site` other
  than `none`/`same-origin`, a cross-site `Sec-Fetch-Mode`, or a `Host` other than
  `127.0.0.1`, `localhost` or `[::1]`. curl, scripts and the in-pane clients send none of these.
- **IT settings are only trusted when they are IT's.** `%ProgramData%\VibeModel\managed.json` is
  used only when file and folder are owned by Administrators or SYSTEM and not writable by ordinary
  users; otherwise it is ignored and Diagnose says so (see docs/deployment.md).
- **Secrets at rest.** The user's API key is DPAPI-encrypted for the Windows user; a key in
  managed.json is plain text by design and must be protected by the file's ACL.

## What the AI can do

The assistant drives Revit through the same server. It can create, change and delete elements,
run `exec`, read the project folder and write the project memory/journal. Every modification is one
Ctrl+Z. Review what it did; the chat pane shows each command it ran.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through
[GitHub's private vulnerability reporting](https://github.com/Quincy987/VibeModel/security/advisories/new)
on this repository. Say which version (`diagnose` prints it), what you did and what happened.
You will get an answer, and a fix in the next release when the report holds.
