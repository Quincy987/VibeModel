# Verify — server lockdown, live in Revit 2023 (2026-10-16)

Merged tree: `claude-bot/integ-oct16` = development + repo-hygiene + plan12-live + server-lockdown
(unit tests: 1324 pass). Build: `build.ps1` (Revit 2023, DLL deployed 12:20). Two Revit instances
of this session: port 18884 (Home, no document) and 18885 (a test project open). The per-session
token was read from `%LOCALAPPDATA%\VibeModel\servers\<port>.json`.

## (a) Token — default on

| Request | Result |
|---|---|
| `GET /help`, no header | `401 Unauthorized` — `ERROR: Missing or invalid token.` + `Hint:` naming `servers\18884.json` |
| `GET /help`, header with the file's token | `200 OK`, the help text |
| `GET /help`, wrong token | `401` |
| `GET /info?format=json`, no header | `{"ok":false,"error":{"code":"unauthorized","message":"Missing or invalid token.","suggestion":"… \"token\" field of …\\servers\\18884.json."}}` |

The servers file carries `{"port","pid","startedUtc","token"}`; a second Revit instance got its own
port (18885) and its own token.

## (b) Browser headers — refused before the token

| Request | Result |
|---|---|
| `Origin: http://evil.example` + a valid token, `/info` | `403 Forbidden` — `ERROR: Requests from a web page are refused (Origin: http://evil.example).` + `Hint: Only local tools may drive this server …` |
| `Sec-Fetch-Site: cross-site`, `Sec-Fetch-Mode: no-cors` on `/health` | `403` |
| The `<img>` case: `GET /exec?args=…` with `Sec-Fetch-Site: cross-site`, `Sec-Fetch-Mode: no-cors`, `Sec-Fetch-Dest: image`, no token | `403` (nothing executed) |

## (c) Host check

| `Host` header | Result |
|---|---|
| `evil.example:18884` (valid token) | `403` |
| `localhost:18884` | `200` |
| `127.0.0.1` (bare) | `200` |
| `127.0.0.1:9999` (wrong port) | `403` |

## (e) Batch body with multi-byte characters

`POST /batch` on 18885, body 13 319 bytes (30 lines of `exec sb.AppendLine("°m²ë×60");`, UTF-8,
so the 4 KB read boundaries fall inside multi-byte sequences three times): `HTTP 200`, every `°`,
`m²` and `ë` came back exactly twice (echoed command line + exec output: 3600 each), 0 replacement
characters, 0 `ERROR` lines, and the read-only batch created no undo entry (`-- model #2` before
and after).

## (d) Chat pane

See the bottom of this file — filled in from the live turn.

**Result (2026-10-17 02:33, focus-free UIA: ValuePattern on the input + Send invoked).** The pane
sent "Hoeveel muren staan er in dit model, en van welke types?" through the Claude Code backend.
The CLI started and reached Anthropic, which answered: *"Your organization has disabled Claude
subscription access for Claude Code — Use an Anthropic API key instead, or ask your admin to enable
access."* So the pane → CLI path works up to the model call, but the organisation's policy on this
account blocked the turn before any command ran; the token hand-off to the CLI (`VIBEMODEL_TOKEN`
in the child's environment, the header in its prompt) is covered by the unit tests
(`ServerLockdownTests`), not by this live turn. The in-process backends (API / local LLM) read the
token directly and need no CLI; the API backend could not be exercised live (no API key on this PC).
