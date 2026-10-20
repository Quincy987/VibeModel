# Changelog

## [Unreleased]

### Added — Update from inside Revit
- Plan 19. When a newer version is published, the chat pane shows a slim banner: *VibeModel 1.2.15
  beschikbaar (2,6 MB)* with **Bijwerken** (Update), *Wat is nieuw* and *Niet nu*. Bijwerken downloads
  the release zip in the background, checks its size and SHA-256 against the published manifest, puts
  it in its own version folder and says *klaar - actief na het opnieuw starten van Revit*. Revit is
  never restarted for you and nothing in the model changes. A download that does not match the
  manifest is refused and nothing is installed. Measured in Revit 2023 with unsigned test builds:
  each new version was active after the next start, and an update did not make Revit ask *Always
  Load* again.
- A small, stable **loader** is now what Revit starts (`VibeModel.Loader.dll`, same add-in id): it
  starts the installed version from `%LOCALAPPDATA%\VibeModel\versions\Revit<year>\<version>\`, or a
  developer build right next to it, and writes one line per start to `logs\loader.log`. Updates add a
  version folder and never replace the loader — the only file Revit's security prompt is about.
- Settings → *Versie en updates*: installed version and loader, where it runs from, the update source,
  *Automatisch controleren op updates* (once a day, at start), *Nu controleren*, and *Terug naar
  <version>* (rollback at the next start; the previous version is kept). Diagnose has an `== Updates`
  section; `update`, `update --install`, `update --rollback` do the same from the chat or a terminal.
- For IT: `managed.json` `DisableUpdates` (switch it off) and `UpdateSource` (your own share or https
  mirror of the release assets). Each release now carries `latest-Revit<year>.json` and a `.sha256`
  next to the zip, and the loader is signed along with the add-in. See *Updates* in docs/deployment.md.

### Changed
- `install.cmd` installs the loader into the add-ins folder and the add-in into its version folder,
  and removes the add-in an older install left in the add-ins folder. A new version alone installs
  while Revit runs (active after a restart); Revit only has to be closed when the loader changes or
  such old files must go. It never downgrades silently: an older zip is installed next to a newer
  version, which stays active (`-AllowDowngrade` to switch on purpose) — a login script that re-runs
  the same zip cannot undo an in-app update. This first release with the loader is installed with
  `install.cmd` once; later versions can come through the app.

## v1.2.13 — Start a New Chat in Time (2026-10-20)

### Changed — the chat tells you when to start a new chat
- A colleague hit their Claude subscription's session limit after an answer that showed
  "16.6M tokens · ≈ €33" and asked what the tokens meant. Claude re-reads the whole conversation at
  every step, so a long chat makes every following question expensive — and people new to AI keep
  one chat going all day.
- The line under an answer no longer shows euros: it reads `3.9M tokens`, with `· lang gesprek` and an
  orange dot once the conversation is about twice a fresh chat's size, `· zeer lang gesprek` and a red
  dot from three times. The tooltip explains what the tokens are, the conversation length, and the
  API-price estimate (which a subscription does not bill per question).
- Once per chat, after a very long one, a bar under the answer explains why and offers **Nieuwe chat**
  (or **Niet nu**). The welcome card of an empty chat ends with the tip to start a new chat per task
  (not with a local model, which has no limit).
- Length is judged on how much the turn's last call carried, not on the turn's total — a single first
  question with many tool steps is not a long conversation.

## v1.2.12 — Known API Gaps, Labels You Can Finish (2026-10-20)

### Added — `apilookup` names what Revit's API cannot do
- Plan 18, from a colleague's rebar-tag session: the chat added the shared parameters and the tag
  types but could not make the labels, and only found the workaround when asked. Now `apilookup`
  (and the hint under a failed `exec` compile) answers questions about labels in tag, generic
  annotation and title-block families, legend views, and creating phases or design options with a
  `Known gap:` block: what the API cannot do, what does work (measured in Revit 2023: add the family
  parameter, copy the nearest label, put `(LABEL <parameter>)` next to it, load the family back) and
  the step the user does by hand (Edit Label). It also warns that setting a label's `Label` parameter
  reports success and changes nothing. No new prompt rule — only `apilookup`'s one-line description
  names the gaps.
- An `exec` snippet can now load a family it edited back into the project:
  `fd.LoadFamily(doc, new VibeModel.Services.Helpers.KeepValuesLoadOptions())` (the options generated
  families already used, now public; Revit refuses the reload without options).

### Fixed
- `profile <name> --points …` on an existing profile said "PROFILE UPDATED" but every later `extrude`
  / `sweep` kept using the old points: the reload keeps the project's type values, so the stored
  points and the change stamp stayed old. Both are now written onto the project types after the
  reload (the loader writes the stamp for every generated family, as `extrude` already did by hand);
  `profile <name>` shows the new points and a repeat with the same points says "Unchanged".
- The model-state stamp no longer reports "1 USER edit" for a change an `exec` snippet made through
  a Revit call that opens its own transaction (loading a family back): whatever is committed while a
  VibeModel command runs counts as VibeModel's.

## v1.2.11 — Approve Before Keeping, Composite Profiles, Server Lockdown (2026-10-19)

### Added — approve before keeping, preview, read-only runs (Settings → "Wijzigingen door de AI")
- Plan 15, after pyRevit 7.0's agent runtime (2026-10-06). **Off by default**: `auto` keeps every
  answer exactly as it was. `ask` makes deletions and edits (`delete`, `exec`, `set`, `join`,
  `unjoin`, `phase`, and batches holding one) wait for the user: Revit isolates and selects what
  changed and a window shows the question, the command and the counts by category; **Behouden**
  keeps it as ONE undo step, **Ongedaan maken** (or Esc / closing) rolls everything back and tells
  the AI not to retry. "Deze Revit-sessie niet meer vragen" switches it off until Revit closes.
  New elements never ask. `readonly` refuses every change (`READ_ONLY`, with what it would have
  changed) — for colleagues who only look.
- `?preview=1` on any modifying command or batch: it runs, says what it would add/modify/delete,
  and rolls everything back (an annotation it would re-host or remove is reported as `-- preview:`).
  `?readonly=1` (meant for `exec`/`batch`) fails (`QUERY_MODIFIED_MODEL`) instead of changing the model.
- `approvals` lists this session's decisions; a decision a client missed (it waits up to 10 min,
  then `APPROVAL_PENDING`) appears once in the next model-state stamp. Discarded runs are journaled.
- The file-based fallback (only used when the HTTP port could not open) cannot ask or roll back, so
  under `ask` or `readonly` it refuses changes instead of slipping past the setting.
- IT can set it per machine: `ChangeApproval` in managed.json (locked in Settings); Diagnose names the
  source; `build.ps1 -Approval ask` for testing. Measured in Revit 2023: a window in 0.3–1.1 s, Keep is
  one Ctrl+Z (pile; floor + its spot elevation; an exec move), Discard leaves the edit counter as it was.

### Added — `apilookup`: the Revit API of this Revit, for exec
- Plan 16, after pyRevit's `lookup_revit_api`. `apilookup Wall`, `apilookup Wall.Create`,
  `apilookup FilteredElementCollector --filter Where`: namespace (and the full name to write in exec
  when exec does not import it), how to get one (the collector line goes through a base class for
  Room, ModelLine and the other classes Revit's class filter refuses), properties, methods by overload, enum values and
  `[Obsolete]` with Revit's own replacement advice — reflected from the RevitAPI of the running year.
  Unknown names give the closest ones. 0.28 s the first time, then 0.01–0.03 s.
- A failed exec compile now appends the matching lookup (`Hint: apilookup Wall.Widht … Closest:
  Wall.Width`) — matched by error number, so the Dutch compiler messages work too.

### Added — exec stops runaway loops
- Plan 17. An endless loop in an exec snippet froze Revit until it was killed. Every loop body
  written with `{ }` now checks a deadline: after 25 s (`?timeout=<s>`, up to 300) the snippet stops,
  its transaction is rolled back and exec answers `EXEC_TIMEOUT` — measured 25.1 s, Revit answered
  the next command at once, no measurable overhead on normal loops. Brace-less loops, recursion and a
  single long Revit call are still not interruptible (the timeout message says so).

### Added — quay walls, capstones and curved runs as real families (`profile`, `extrude`, `sweep`, `lwall`)
- Plan 12 phase 1. A composite cross-section used to be an `exec` DirectShape: no family or type,
  no Volume in a schedule, `join` refused. Four commands now generate a real Revit family on the
  fly, load it and place it as ONE undo step (also inside `batch`, atomic included):
  - `lwall x1 y1 x2 y2 --stem 600x4000 --foot 2500x800 [--toe] [--side left|right] [--slope]` — an
    L-shaped quay wall on one parametric line-based Generic Model (`VM_L-wand`, or `… SF` with
    `--category foundation`): stem and foot sizes are type parameters on locked reference planes, each
    size combination becomes a type named from its sizes, an existing type is reused. The line is the
    stem's front face, the heel lies left of the line unless `--side right`.
  - `profile <name> --points u,v …` — a named section stored as a Revit Profile family (also usable in
    Revit's own Wall Sweep / Slab Edge tools) with its points in `VM_Profile`; `profile <name>` prints
    points, area, box and centroid; `profile` lists them.
  - `extrude <profile | --points …> --along <line/wall id | x1 y1 x2 y2>` — line-based Generic Model
    (`--category foundation` = point-based Structural Foundation with Wall length, `framing` = a beam);
    `extrude … --vertical <height> --at <x y>` stands a plan profile up (Height is a type parameter).
  - `sweep <profile> --along <id,id,…>` — along a chain of lines and arcs (any order) or a wall; the
    path is fixed in the family and the response says so.
- Profiles are mm, u = left of the run direction (the side `section` looks to), v = up; outlines are
  closed, cleaned and checked (a crossing outline names its segments).
- Every generated family schedules by type with a correct Volume and a shared `VM_Length` (fixed
  GUID, so all of them share one schedule column); `Material` is a type parameter (`--material`, or
  the pack's default). The new element is joined with the walls, floors and generated elements it
  overlaps (`--no-join` to skip; library families such as piles are never joined), gets the office
  phase (`--phase`), runs the post-command check and is journaled with the `.rfa` it wrote.
- Re-running updates instead of adding "(2)": the same profile reuses the loaded family, a changed one
  is regenerated and reloaded keeping the project's type values; a family edited by hand in Revit
  since (or one VibeModel did not make) is refused until `--replace`. Live-checked in Revit 2023
  (`docs/verify/plan12-live-2026-10-16.md`): a reused family is not opened, so its hand edits show
  up as a `WARN` when Revit's volume differs from profile area × length; `--replace` regenerates
  even with unchanged points.
- The `.rfa` files go to `<project folder>\VibeModel\families\` (3 s deadline; offline → a local copy,
  said in the response). Revit's shared-parameter file setting is swapped for the build and always
  restored.
- Office packs get an optional `profiles` block (prefix, category per kind, templates, folder,
  shared parameters, materials, type-name pattern) and the example pack a `kadeconstructie` topic
  sheet: the questions an office answers about its quay walls, without values.

### Security — the local server only answers the user's own tools
A brainstorm review (2026-10-15) found that any web page the user had open could drive Revit: the
server never looked at `Host` or `Origin`, and the token was off unless `VIBEMODEL_TOKEN` was set —
`<img src="http://127.0.0.1:18884/exec?args=…">` ran C# inside Revit, and a domain resolving to
127.0.0.1 could also read `readdoc`/memory. See the new `SECURITY.md`.
- **Browser requests are refused** (403 `forbidden`, before `/health` too): any request with an
  `Origin` header, a `Sec-Fetch-Site` other than `none`/`same-origin`, a cross-site `Sec-Fetch-Mode`,
  or a `Host` that is not `127.0.0.1`, `localhost` or `[::1]` (bare or with the server's own port).
  curl, the chat backends and the Claude Code CLI send none of these — nothing legitimate changes.
- **A token on every call, by default.** Without `VIBEMODEL_TOKEN` the server now generates a random
  per-session token and publishes it in the per-user `%LOCALAPPDATA%\VibeModel\servers\<port>.json`
  (`token` field). The chat pane reads it in-process, the Claude Code CLI gets it as `VIBEMODEL_TOKEN`;
  terminal one-liners for bash and PowerShell are in CLAUDE.md/README. An explicit `VIBEMODEL_TOKEN`
  still wins. `"OpenServer": true` (managed.json or settings.json) restores the open server for
  scripts; Diagnose shows which mode is active, and a 401's hint names the servers file.
- **managed.json must be IT's.** `%ProgramData%\VibeModel\managed.json` is only trusted when file and
  folder are owned by Administrators or SYSTEM and give ordinary users no write access (Windows lets
  users create folders under ProgramData); otherwise it is ignored, logged once and named in Diagnose.
  docs/deployment.md shows the `icacls` commands.

### Fixed
- `POST /batch`: a multi-byte character (`°`, `m²`, `ë`) that straddled a 4 KB read boundary was
  decoded as two broken characters and changed the body's byte count. The request is now read as
  bytes against Content-Length and decoded once; `Expect: 100-continue` (curl, bodies over 1 KB) is
  answered before the body is awaited.
- `scripts/sign.ps1` signed silently without a timestamp when the timestamp server was unreachable
  (such a signature dies with the certificate). It is now an error unless `-NoTimestamp` is passed.
- `poll-health.ps1` and the build summary use `127.0.0.1` instead of `localhost` (IPv6 stall).


## v1.2.10 — Zero-Admin Onboarding, Annotations Survive Replacements (2026-10-14)

### Changed — installing for people who are not programmers (no admin, no terminal)
Feedback from a colleague who installed it: the add-in landed, but "Node.js to connect Claude" was
nowhere to be found. Installing developer tools is not something modellers do, and office laptops
have no administrator rights.
- **Connect to Claude, inside Revit.** An unconnected chat shows a wizard instead of npm commands:
  paste an API key (checked against the API at once, free: `GET /v1/models/{model}`; a wrong key is
  never stored), or install Claude Code with Anthropic's per-user installer in a visible window, log
  in, and switch to it — no restart. Every control has an AutomationId.
- **An API key wins.** `auto` now prefers a configured key (managed.json, `ANTHROPIC_API_KEY`, or the
  user's own), then Claude Code, then a local model. Default model `claude-sonnet-5-5`.
- **The user's own key is never plain text on disk:** DPAPI-encrypted for the Windows user
  (`AnthropicApiKeyProtected`); an old plain-text key is encrypted on first read and removed. (A key
  IT puts in `managed.json` is plain text there by design — protect that file with an ACL.)
- **IT can manage it:** read-only `managed.json` (`%ProgramData%\VibeModel\` then `%APPDATA%\VibeModel\`)
  with `PreferredBackend`, `Model`, `ApiBaseUrl`, `AnthropicApiKey`, `DisableClaudeCode`, `ClaudeCliPath`,
  `Language`; managed fields are locked in Settings ("beheerd door IT"). `ApiBaseUrl` lets a gateway
  that speaks the Messages API stand in for api.anthropic.com. See docs/deployment.md.
- **Office networks:** every API call (chat, quick replies, distiller, key test) goes through the
  Windows proxy, authenticating to the proxy with the user's Windows credentials, with TLS 1.2 or
  newer (Revit's process-wide TLS setting is only touched when it pins older versions) and the
  Windows certificate store. Claude Code gets the Windows proxy as `HTTPS_PROXY` when none is set,
  plus `NO_PROXY` for 127.0.0.1/localhost so its calls to Revit never go through the proxy.
- **Claude Code found where the native installer puts it** (`%USERPROFILE%\.local\bin`) and on a PATH
  read fresh from the registry; `ClaudeCliPath` / `VIBEMODEL_CLAUDE_CLI` set it (`none` = off).
  Without Git for Windows the CLI shells out through PowerShell: it is now allowed, and the prompt
  says to call `curl.exe` there.
- **Diagnose** (chat header + `diagnose` command): versions, signature, backend, key source (masked),
  where Claude Code was looked for, proxy, DNS/HTTPS to api.anthropic.com and claude.ai with the
  certificate issuer, recent warnings/errors, settings with secrets masked; Copy / Save.
- **Cost line** under each answer: `31.0k tokens · ≈ €0,12` (API: list prices in `ModelPricing`;
  Claude Code: its own `total_cost_usd`). Never stored with the chat.
- **Settings → Anthropic API:** model and endpoint fields; *Validate* uses the same free key check as the
  wizard, against the endpoint typed in the box (a network failure names that host), and tests IT's /
  the environment's key when the box is empty. Such a key is shown masked and never copied into settings.json. The Claude Code tab finds the
  CLI the way the backend does (off the UI thread) and no longer mentions npm.
- Warnings and errors are always logged now (INFO stays behind `VIBEMODEL_DEBUG=1`), so support has a log.
- Header: "Niet verbonden" in the pane's language; the buttons stay visible in the default narrow pane.

### Fixed — the API backend on models that think by default
- Sonnet 5.5 (the new default) thinks by default. Its thinking blocks are now kept and sent back with
  the turn (they were dropped, which breaks a tool loop), the system prompt is frozen for a whole turn,
  screenshots and attachments are trimmed only between turns — the newest screenshot stays an image
  for follow-up questions — and then stale thinking is removed; a 400 about thinking blocks is
  answered once by removing them and retrying (only when there are any). A refusal and a cut-off
  answer are explained instead of returning nothing. `max_tokens` 32000.

### Installer and release zip
- `install.cmd` asks nothing but "close Revit" (no API-key prompt any more), is idempotent ("already up
  to date"), logs to `%LOCALAPPDATA%\VibeModel\logs\install.log`, reports the signature, and has
  `-Quiet` (IT: no questions; exit 2 when Revit runs, 3 when the build's Revit is missing) and
  `-InstallClaudeCode` (Anthropic's installer in its own PowerShell).
- **Only the Revit the build is for.** The zip carries `revit-year.txt`; the installer installs into that
  Revit year only and names the others it leaves alone. The old script copied the Revit 2023 build into
  every Revit it found, so a laptop with Revit 2025 as well got an add-in 2025 cannot load.
- `scripts/sign.ps1` (Set-AuthenticodeSignature + timestamp) and `package-release.ps1 -Sign -Thumbprint`.
  **Measured in Revit 2023:** unsigned → one "Always Load" prompt per new version; a trusted signature
  → no prompt at all; a self-signed (untrusted) signature → "Security - Invalid Signature … may have
  been tampered with" on every start, with only Load / Do Not Load. So packaging refuses to sign
  without a real certificate (`-AllowSelfSigned` for tests only), and the installer does not offer to
  trust a certificate (Windows itself warns that adding a root is a security risk).

### Added — annotations survive replacements
- Seen during the demo recordings: the chat put spot elevations on a capstone and a pile tip, then in
  a later turn replaced those elements — the spots silently vanished or broke. Measured in Revit 2023:
  most host changes through the API raise no failure. Deleting a floor deletes its spot and a 3-reference
  dimension; a wall+floor dimension survives with only the wall references (0 segments); a pile's
  parameter change can leave its tip spot with a stale value; shape-editing a floor invalidates its spot.
- Around `delete`, `exec`, `set`, `join`, `unjoin` and `phase` (and any batch with one of them) the
  spot elevations, dimensions and tags on the elements involved are recorded (a type id covers its
  instances) and afterwards re-hosted: refreshed on the same element when it moved or went stale, else
  moved to a replacement — an element of the same category that the command added or VibeModel created
  earlier this session, never an unrelated neighbour (spots: a face/edge at the same height ±1 mm within
  50 mm; dimensions: a face on the old reference plane ±1 mm; tags: the replacement covering the old
  element's middle). An annotation the command deleted itself stays deleted. Whatever cannot be
  re-hosted is deleted, never left invalid. One line says what happened: `-- annotations: re-hosted …;
  removed …` (JSON `annotations`), `(Revit)` when Revit's failure policy deleted it. Same undo step,
  except `exec` (its own step). Verified live: piles 320→350 replaced by `placeid` + `delete` (both
  pile spots and the floor-pile dimension re-hosted, values unchanged), the floor recreated by `exec`
  (spot + both dimensions re-created with all references), a pile moved (the spot followed it, no
  line), the floor raised by `set` (spot follows, no line), the floor shape-edited by `exec` (warning
  dismissed, spot re-hosted), a spot an `exec` deleted on purpose (stays deleted, no line), and in a
  section showing levels a floor recreated by `exec` (its face+level dimension and its tag re-hosted).
- `spot <x> <y> | spot <id> top|bottom [--leader x y] [--fixed] [--type name]` — a spot elevation
  (peilmaat). It snaps to a face the section cuts, else the edge the view shows, and always to an edge
  of a family instance (Revit refuses instance faces and faces behind the cut). The response says what
  it is attached to and warns when that element is likely to be replaced; `--fixed` hangs it on a
  detail line that never moves.
- `whatdepends <ids|selected>` (read-only) — the annotations (per view and sheet), hosted and joined
  elements that depend on elements, with a verdict, before you replace them.
- `dimension` prefers a level, grid or reference plane at the face's position (±1 mm; the faces
  themselves when Revit refuses that), lists `Attached to:` per reference and notes when a reference is
  likely to be replaced; JSON references get `stable` and `vibeModelCreated`. `tag` says what it tags; `text --leader` says its leader is a free
  point.

### Fixed — dimensions and spots on family instances hung on the family symbol
- Faces and edges of a family instance's geometry carry references to the family symbol. `dimension`
  used them as they were, so a dimension to a pile was attached to the symbol: it outlived the pile and
  no scan for the pile found it. Both `dimension` and `spot` now use the instance's own reference to
  the same face/edge.


## v1.2.9 — Revit Failures Without Dialogs, IFC Read-Back (2026-10-13)

### Fixed — a Revit failure dialog no longer hangs every command
- Seen live during the demo recordings: a command or `exec` snippet that made Revit post an error
  ("Spot Dimension does not lie on its reference", "One or more Spot Dimension references are no
  longer valid") or a warning in a transaction without a handler ("Highlighted walls overlap" —
  `wall` already dismissed warnings, so most likely an `exec` snippet's own transaction) opened
  Revit's modal failure dialog. It was invisible with Revit minimized or driven headless, the
  request timed out after 30 s, every following command timed out too, and the chat turn ended
  with "Revit hangt in een dialoog".
- VibeModel now handles failures itself while a command runs (`RevitFailurePolicy`): warnings are
  dismissed and reported (`-- revit warning: …`). An error whose only failing elements are
  view-specific spot dimensions, dimensions or tags is resolved with Revit's "Delete Element(s)",
  which keeps the change (`-- revit resolved: … → deleted 1 annotation: <id> (Spot Elevations)`).
  Any other error rolls back and returns `ERROR: Revit rolled the change back: <Revit's reason>`
  with a `Hint:` line (`join` keeps its own refusal report, with that reason in it). When one
  failure forces a rollback, nothing else of that pass is applied. JSON gets an additive `revit`
  object, also per batch command. This covers VibeModel's own transactions (preprocessor) and
  transactions an `exec` snippet opens itself (`Application.FailuresProcessing`, only while a
  command runs — over HTTP or the file-based fallback; your own work in Revit keeps the normal
  dialogs).
- A transaction Revit rolled back during commit is now an error instead of a success (the commit
  status used to be ignored). Live example: `set <wall> "Unconnected Height" 1` reported success while
  Revit silently undid it; it now says `Revit rolled the change back: Revit gave no reason …`.
- Any dialog that still opens during a command is logged with its id.
- A failure Revit keeps re-posting after it was resolved is rolled back after 10 rounds instead of
  looping (`FailureLoopGuard`); a snippet whose many transactions each resolve a different
  annotation is not mistaken for such a loop.

### Fixed — "the IFC has no piles"
- **`export ifc` reads its own file back.** The piles were there: Revit 2023's default IFC class
  table (`exportlayers-ifc-IAI.txt`) maps Structural Foundations to `IfcSlab BASESLAB`, so 510 piles
  arrived as slabs and a search for `IFCPILE`/`IFCFOOTING` found nothing. The response now lists per
  category how many Revit elements went in and as which IFC class, which model elements in scope are
  missing (with the usual causes: a category mapped to Not Exported, `Export to IFC` = Don't Export,
  the export phase), and names the foundation mapping with the fix (`Export Type to IFC As`).

### Changed — shorter chat answers
- Answers ran to 6–8 lines: a closing list of 2–3 next steps (asked for by two older prompt lines)
  and, in nearly every answer, the reminder that the work view is on the existing phase. The rules
  now count notes, warnings and offers in the 5 lines, allow ONE closing offer line (the quick-reply
  chips turn it into buttons), mention the view's phase only when this turn's work is hidden by it,
  and drop the "converted to m" and "one Ctrl+Z" restatements.

### Fixed — installer that could not be read ("I ran install.ps1 and see no tab")
- A colleague downloaded GitHub's *Source code (zip)* — the only zip on the releases page, since no
  release since v1.2.1 had an add-in zip attached — ran `install.ps1`, and the window closed on
  the "Cannot find VibeModel.dll" error before it could be read. The installer now stays open at the
  end and after every error, recognizes the source-code zip and names the right download, asks to
  close a running Revit (it keeps the DLL locked), and verifies afterwards that every file is in
  place and unblocked. The API-key prompt says Claude Code users just press Enter, and saving a key
  no longer overwrites the other settings or pins an old model id.
- `scripts/package-release.ps1` builds and packs `release/VibeModel-v<version>-Revit<year>.zip`
  (add-in, PdfText helper, install.cmd/.ps1, README.txt with the three steps) — the asset every
  release gets from now on. README: download that zip under *Assets*, double-click `install.cmd`.

### Docs
- CLAUDE.md: examples for `filter`, `measure`, `whereused`, `zoomto`, `isolate`/`unisolate`,
  `hide`/`show`, `colorsplash` and `chatlog` (the only commands without one).
- `chatlog` gets its step label in both languages; a test now checks every registered command has one.


## v1.2.8 — Chat Tables, Sources, Units & Suggestion Chips (2026-10-12)

### Added — suggestion chips that follow the context
- **Starter chips** in an empty chat follow the open model instead of a fixed list: what you have
  selected, an open sheet, an active office standard and documents in the project folder come
  first, then the general examples.
- **Quick replies** after every answer: a separate, cheap model call (Haiku through the API, the
  Claude Code CLI, or the local model when tool use is on) guesses what you were about to type next
  and offers none, or two to four one-tap chips above the input. A chip that only asks or selects is sent at once; one
  that changes the model (marked with a pencil) is put in the input box so you check it and press
  Send. "No thanks"-style buttons are filtered out in code; the prompt also tells the model not to
  invent numbers and to offer no row after a plain fact (live: a one-line fact got none). The row
  steps aside while you type and is never stored in the chat. The mechanism is a transfer of
  DegenAI's quick replies (same prompt structure, schema and sanitizer, Revit domain).

### Added — tables, units and sources in chat answers; a compact welcome card
Workshop feedback (werkvoorbereiders, 2026-10-12): tables showed as raw pipes, numbers came
without units, nobody could read back *why* a choice was made, and the welcome text was English
and took a third of the pane.
- **Tables.** Markdown pipe and grid tables render as a real table: bold header on a light fill,
  alternating row tint, padded cells, numeric columns (a number with an optional unit) right-aligned,
  column widths fitted to the pane like a browser table (numbers never break, long text wraps, no
  sideways scrolling; refit when the pane is resized). Long
  tables show 30 rows and "… nog N rijen" unfolds the rest; **Kopiëren** under each table puts it on
  the clipboard as tab-separated text (pastes into Excel as cells). The rules tell the model to use a
  table for more than 3 items with 2+ attributes, at most 4 columns, with units in the header cell.
- **Units.** New rules: every quantity names its unit, VibeModel output is mm unless stated, values
  over 10 000 mm go to m / m² / m³ (said once). Command output now names the unit wherever it printed
  a bare number: `params` (and `family`, `set`, `colorsplash`/`filter --count` labels) classify a
  double parameter by its Revit spec — lengths in mm, areas in m², volumes in m³, angles in ° —
  where areas, volumes, angles and non-"dimension"-named lengths used to print raw internal units;
  `geometry` volume in m³; `section`, `viewport`, `sheetinfo`, `tag --at`, `pcprobe` and the `curves`
  headers name their mm. **`set` and `filter` read a typed length, area, volume or angle in the unit
  `params` prints** (mm, m², m³, °; before: an angle in radians, an area in ft²). Other measurable
  kinds (force, slope, …) still take Revit's internal units, as before.
- **Sources.** The model cites what it took from the office standards, the leidraad, a project
  document or the model with a marker after the sentence — `[bron: leidraad 4.2]`,
  `[bron: standaard phasing]`, `[bron: document memo.pdf p.3]`, `[bron: revit 1543814]`,
  `[bron: revit-api]` — and never invents one. The pane shows each as a small superscript number
  (tooltip = the source) with a collapsed "Bronnen (N)" line under the answer; a click opens the
  source in a popup inside the pane (the `standards` chapter or topic, the `readdoc` text scrolled
  to the page or section) or selects and zooms to the element. The stored chat keeps the raw
  markers, so a reopened chat renders the same.
- **Welcome card.** An empty chat now opens with a compact card in the Settings language: backend +
  model, the open model + its project folder, and four example chips that fill the input box. The
  not-configured card keeps the install hints, shorter and localized. The header's **?** button shows
  the longer example list.
### Added — modelling flags instead of self-written code (workshop 2026-10-12)
The capability overview marked these as "works only via code the chat writes itself"; they are
now first-class flags. Every existing positional form parses as before.
- **`wall … --type <name|id>`, `floor … --type <name|id>`**: exact name, else a unique
  case-insensitive substring, else the id; an ambiguous name lists the matches (with the wall
  kind, so two types both called `Beton_300` — basic and stacked — can be told apart), an unknown
  one lists every type. The response names the type used. Also `--level <name>` on both, and on
  `wall` `--base-offset <mm>`, `--top-level <name>` and `--top-offset <mm>`.
- **`place` / `placeid`**: `--rotate <deg>` (about the insertion point, counter-clockwise from
  +X), `--set "Param=value"` (repeatable, instance parameters, mm/° for plain numbers; a missing
  name is refused with the available names and nothing is placed; a type parameter is pointed at
  `set <typeId>`), `--level <name>`. **`z` now works for level-hosted families**: it was ignored;
  the instance is moved through the offset parameter that actually moves it (the one visible in
  Properties first, each candidate checked against the result) and the response names it.
- **`--along <line id | x1 y1 x2 y2> --spacing <mm> [--count <n>] [--offset <mm>] [--rotate auto|auto+90]`**:
  n instances along a detail line, model line, a wall/beam's location curve or two points, as
  one transaction (one Ctrl+Z), with the ids and the actual spacing in the response. Spacing alone
  fills the line end to end and is reduced to divide it evenly (never more than asked, 1 % slack);
  count alone spreads evenly; both together are exact. Arcs are followed by parameter, and
  `--rotate auto` turns each instance to the tangent there.
- **`look [<3D view>] <n|no|o|zo|z|zw|w|nw|top|iso|x y z> [--at ids|x y z|selected] [--distance] [--pitch] [--margin] [--box]`**
  and **`view 3d --look …`**: the camera stands at that compass direction from the target
  ("camera from the water onto the corner" = `zw`), 30° down, shaded with edges; the crop region
  is refitted to the target as seen from the new camera (a crop fits only one camera), `--box`
  adds a section box. Default target: the model elements in the view.
- **`set <id> "<param with spaces>" <value>`** works, as do `set <id> Base Offset=500` and the
  unquoted `set <id> Base Offset 500` (the longest leading words that name a parameter win); the
  3-token form is unchanged. Plain numbers on length parameters are mm and on angles degrees,
  whatever the project's display units (it used to go through the display units first).
### Added — sheets 2: schedules, callouts, filled regions, views to another sheet, export
The gaps the werkvoorbereiders workshop (2026-10-12) listed for sheet work, as commands:
- **`schedule <category> --fields "Family and Type,Count,Length"`** creates a real Revit schedule
  (hoeveelhedenstaat). Fields are matched by the names Revit lists (built-in, project and shared
  parameters; an unknown name lists every schedulable field of that category). `--filter
  "<field> <op> <value>"` (`= != > >= < <= contains !contains beginswith endswith`, or the
  URL-friendly `eq ne gt ge lt le`; numbers are in the project's display unit), `--group <field>`
  gives one row per value with the quantities summed, `--total` a grand total, `--name`. The
  response prints the first 20 rows as a text table with the units in the headers;
  `schedule <name|id> --rows [n]` reads any schedule back (and says which sheets it is on; an
  empty one names its filter count and phase). Columns are widened to their longest cell so type
  names do not wrap on the sheet; an unknown field shows close matches first.
- **`callout <parent view> --box x1 y1 x2 y2`** — a plan callout (or `--section`: a detail view)
  over a rectangle in the parent's frame, the convention of `crop --box`. The pack's `callout`
  view defaults apply (else those of `plan`/`section`); `--name`, `--scale`, `--template`/
  `--no-template`. The response gives the scale and the paper size at that scale.
  `--reference <view>` makes a reference callout to an existing view instead.
- **`region <view> --points x,y x,y x,y …`** — a filled region from a closed polygon in the view's
  own frame (`--type` picks the filled region type; crossing or zero-area outlines are refused
  before Revit sees them); **`region <view> --detail "Family : Type" --at x y [--rotate deg]`** places
  a detail component. **`list regions [--view]`** reads both back (type, area, view).
- **`viewport --move <id> --to <sheet> [--at x y]`** moves a placed view (or schedule) to another
  sheet in one transaction, keeping scale, viewport type (title on/off), rotation and detail
  number. On a sheet of a different size a view that cannot fit is refused with the scale that
  would fit; nothing is moved. The viewport gets a new id, which the response gives.
- **`export pdf|dwg|ifc <sheets|views> | --all-sheets`** in one go: PDF through Revit's own PDF
  exporter (one file per sheet, sheet size; `--combine` for one file), DWG with the office pack's
  saved setup (`export.dwgSetup` in pack.json; `--setup` overrides; otherwise views merged into the
  sheet, no xrefs), IFC with the default setup (the whole model, or one view's elements). Files
  go to `<project folder>\export\` (`--to` changes it), named `{number} - {name}` (`--name` with
  `{number} {name} {project} {date} {format}`). The response lists every file written with its
  size; a target file that is locked (an open PDF) is named and skipped. `export` gets a 300 s
  request timeout instead of 30 s.
### Added — project memory and project journal (plan 08, project part)
- **Project memory.** `remember <fact>` saves one dated line ("doorsnedes krijgen fase 2.
  Bouwfase") to `VibeModel\memory.md` in the project folder (plan 10; models without a folder use
  `%LOCALAPPDATA%\VibeModel\projects\<title>\`). The same fact (case, spacing, quotes, trailing
  punctuation ignored) is not saved twice. `memory` lists the facts numbered, `forget <n|text>`
  removes one, `memory --clear` empties the file. Plain markdown, editable by hand.
- **Every chat turn carries a `PROJECT MEMORY` block** (≤ 2 kB, newest last, "+N older — run
  memory" when cut) next to the PROJECT FOLDER block, on all three backends, with the rule: save a
  project decision/preference/correction with `remember` (one line, the user's words) and say so;
  read PROJECT MEMORY before answering "last time" questions.
- **Project journal** (`VibeModel\journal.md`): the add-in appends one line per modifying command
  or batch that changed the model — time, command + args, ids created/modified/deleted and the
  chat question behind it — and one line per chat turn with the sources its answer cited
  (`[bron: …]` markers). The chat backends send the turn in an `X-VibeModel-Turn` header; the
  Claude Code CLI's own curls are attributed to the turn in flight. `journal [today|<date>|--last
  <n>]` reads it back. Rotates at 2 MB to `journal-<yyyymm>.md`. Lines are written by a
  background writer with a 3 s deadline; an offline folder drops them with one logged warning —
  Revit's main thread never waits on the folder.
  `export` changes no element but is journaled too, naming the files it wrote.
- **Settings → "Projectgeheugen en journaal bijhouden"** (default on). Off: no block, no journal,
  and the four commands are left out of every prompt and tool list — the chat exactly as before.
- `docs` no longer lists the `VibeModel` subfolder (VibeModel's own data, not a reference document).

## v1.2.7 — Orientation Checks, Revit/IFC Links & Chat Workshop Fixes (2026-10-12)

### Added — reviewer feedback: orientation rules and links by shared coordinates
- **`project-north-true` check** — the project base point's *Angle to True North* must be 0:
  Project North never rotates; a straight view comes from the scope box or crop region. `coords`
  prints a WARN next to the angle when it is not 0 (JSON: `baseAngleZero`).
- **`reference-point-true-north` check** — the reference point family (name contains
  `familyPattern`) stays unrotated and pinned. While Project North itself is rotated it reports
  INFO rather than judging an angle that would be measured against the wrong north.
- **`links-by-shared-coordinates` check** — once the model has shared coordinates, every Revit
  link must sit where *Auto – By Shared Coordinates* puts it (compared through both models'
  coordinate transforms; any of the link's named locations counts). Unloaded links and CAD links
  (the API keeps no record of how they were placed) are counted as not verifiable, never flagged.
  Also runs on the link a command just made. A shared link dragged afterwards still reads as
  shared — Revit moves the link's own shared position along with it.
- **`linkrvt "<path.rvt|.ifc>"`** — link another Revit model or an IFC by shared coordinates and
  pin it; refused (with the reason) while the model has no shared coordinates, `--origin`/`--force`
  link origin-to-origin. An IFC goes through Revit's converted `<file>.ifc.RVT` copy (reused unless
  the IFC is newer, then converted again). When placed by shared coordinates, the
  response states where the link landed, read back from the model. IFC linking is not yet verified
  in Revit (no IFC file was at hand).
- `linkcad` names the criterion it placed by ("coordinates set up: base point N/S … E/W … ≠ 0")
  and its first-link hint says "do not rotate — Project North stays True North". The `pinned`
  rule in the example pack also covers Revit links.
- Fixed: after a failed shared-coordinates link, `linkcad` suggested `--first`, which has no effect
  once the model has coordinates; it now suggests `--origin` (and moving, never rotating) there.
### Fixed — chat refused deletes the user had approved
- **The Claude Code backend now runs the CLI with `--permission-mode dontAsk --allowedTools Bash,Read,Grep,Glob`.**
  Without an explicit mode the CLI inherited the user's own `defaultMode`; with `"auto"` its
  safety classifier overrode the `Bash` allow and refused `curl …/delete` — and then even
  `select` — as *Irreversible Local Destruction*, although the user had said yes (workshop
  2026-10-12). Reproduced headless by replaying that session against a dead port: two classifier
  denials with the old flags, none with `dontAsk`; `delete`, `batch delete`, `exec` and `select`
  all run, while Write/Edit/WebFetch stay denied. The read-only Grep and Glob are allowed too: the
  model sometimes searches the command file with Grep, and a denial showed as a failed step. A CLI too old to know `dontAsk` now gets a clear
  "update with `claude update`" message instead of a silent stale-session reset.
- The prompt states that every VibeModel modification is one Revit undo step (Ctrl+Z), so a
  delete or edit the user asked for is executed — never refused or handed back as manual steps.
  The old "always confirm destructive operations" bullet is gone.

### Added — chat language, ask-first rule, compact answers
- **Settings → Taal / Language** (`Language` in settings.json: `nl` default, `en`). The model
  replies in the language the user writes in and falls back to the setting; Revit UI terms stay
  in English as Revit shows them. The pane's fixed texts (status verbs such as "Wand maken…" /
  "Elementen verwijderen…", backend and error messages) come in both languages via a small
  `Strings` helper.
- **One rules block for every backend** (`ChatBackendBase.BuildBehaviourRules`): language, undo,
  *ask first* — confirm in one sentence before a change that lands on a sheet or on shared project
  resources (views on sheets, view templates, materials, families/types, shared parameters,
  phases, worksets); working views, new views, colour overrides, selections, temporary isolates
  and anything explicitly asked for just happen — and *compact answers*: answer first in at most 5
  short lines, no element ids in running text (one `code` line at the end if needed), no headings
  under ~120 words, one short line per step while working. The Claude Code backend sends it in
  every turn's `--append-system-prompt`, so a language switch applies to resumed sessions too.

### Fixed — "Unknown command 'wall%200…'" (arguments in the URL path)
- The Claude Code backend's command file showed every command as
  `curl ".../wall <x1> <y1> …"`, so the model put the arguments in the URL path and got
  *Unknown command 'wall%200%200…'* 3–5 times per turn. The file now opens with a
  **HOW TO CALL** block — the rule *"Arguments always go in ?args=, URL-encoded, or in a POST /batch
  body; never in the path"*, the encoding of space/`+`/`&`/`#`/`%`, encoded examples,
  `curl -G --data-urlencode "args=…"` for code and free text, and `POST /batch --data-binary $'…\n…'`
  (the old batch tip sent a literal backslash-n) — and lists each command as `/<cmd>?args=ARGS   ARGS: <grammar>`.
  Every turn prompt repeats the rule, so resumed sessions get it too.
- **Server safety net:** a path that carries its own arguments — `/wall%200%200%205000%200`,
  `/wall/0 0 5000 0` — now runs as the command with those arguments (logged) instead of failing.

### Added — chat activity block
- Each turn shows a live **activity block** under the question: an indicator with an mm:ss
  counter ("Aan het denken…" before the first token, a quiet "Duurt lang — Revit is misschien bezig"
  hint after 60 s without progress) and one row per tool call — running, done (✓) or failed (✗ with
  the first error line), capped at the last 8. Text the model writes between tool calls is shown
  muted inside the block; only the text after the last tool call is the answer. When the turn ends
  the block collapses to one line ("7 stappen · 48 s", click to expand) or disappears when no tools
  ran. Backends report steps on a UI-only side channel; the stored chat, the token stream and the
  model-bound history are unchanged.
- Answer rendering: more line height and paragraph spacing, list indentation, inline code as a
  pill, smaller headings, code blocks over 20 lines cut to 12 with a "nog N regels" line, a thin
  rule between turns.
- One language source and one label table: the block's texts follow **Settings → Taal / Language**
  and its step labels are the same Dutch/English status verbs (`StatusVerbs`), now also covering
  `whyhidden`, the DWG/point-cloud commands, the office-standards commands and the CLI's own
  file/image/shell tools.

### Added — successcase logbook (scripts)
- `scripts/successcase.py` turns a chat log into a case file (question, answer, per-turn timing,
  command count from the debug log, total time) under `demo_out/successcases/` and rebuilds
  `INDEX.md`; `scripts/successcase_mail.py` starts a case from an Outlook mail (via the bot's
  Outlook helper) and links it to a chat. Re-running keeps Who/Score/Remarks. Protocol:
  `docs/successcases.md`. Cases contain colleagues' names and are never committed (`demo_out/`).

## v1.2.6 — Sheet Assembly, Project Folder, Visibility Diagnosis (2026-10-12)

### Added — `whyhidden` and `join`/`unjoin`
- **`whyhidden` — visibility diagnosis** (`whyhidden <elementId> [viewId]`, view defaults to the
  active one). Answers the werkgroep's recurring "where did it go?" questions from the real view
  state instead of generic advice that throws away work. It walks every Revit visibility gate in
  order and prints one `OK` / `BLOCKED:` / `INFO` / `SKIPPED:` line per check, each blocking line
  carrying a `Fix:` naming the exact setting: category + subcategory visibility and the VG master
  switches, view-specific ownership, per-element Hide in View and Temporary Hide/Isolate, matching
  view filters (visibility checkbox *and* invisible-making overrides), detail level — reporting
  which detail levels the family actually draws at, so "the anchor lines vanish at Coarse" resolves
  to a family visibility setting — view range / far clip against the element's bounding box,
  associated level, view discipline (including Structural views hiding non-structural walls) and
  datum 2D extents for grids/levels, workset visibility, phase filter, design option, crop region
  and 3D section box, and graphic overrides that render something invisible (100% transparency,
  invisible line pattern). Results are cross-checked against Revit's own view element set, so the
  report never claims "all clear" for an element Revit genuinely excludes. A separate `EDITABILITY`
  section covers visible-but-unmodifiable causes (group membership, pinned, linked model, workset
  owned by another user, checkout status). Output ends with a one-line `Verdict:`. Every probe is
  individually guarded, so a view type that refuses a query reports `SKIPPED:` rather than failing
  the command. Report formatting and argument parsing are Revit-free and covered by 21 new headless
  tests.

- **`join` / `unjoin` commands — geometry joins and solid cuts with real diagnostics.**
  `join <idA> <idB>` joins two elements (`JoinGeometryUtils.JoinGeometry`), `--switch` flips which
  element wins an existing join, `--cut` makes A punch a void through B
  (`SolidSolidCutUtils.AddCutBetweenSolids`), and `unjoin <idA> <idB>` removes whichever relation
  exists — join, cut, or both. Every response names both categories, states whether the bounding
  boxes actually intersect (with the overlap size, or the gap and axes when they don't), and says
  which API was called.

  The point is the failure path: Revit refuses these operations with a bare exception or a silent
  no-op. `join` pre-checks `IsAllowedForSolidCut` / `CutExistsBetweenElements` /
  `CanElementCutElement`, translates the `CutFailureReason` into plain language, and prints the
  workaround that actually applies — extend the solids so they really overlap (the sloped wing
  wall against a capstone case), use a void family or Parts when a solid-solid cut is refused
  between system families (the screw pile through a quay wall, the H-beam inside a concrete pile),
  or `--switch` when the wrong element wins. A join that Revit accepts but does not apply is
  reported as a failure rather than a false success. Each operation runs in its own transaction
  and rolls back on failure. Arg parsing, report rendering and the failure explanations are
  Revit-free and covered by 26 headless tests; live behaviour needs Revit.
### Added — Plan 12: Sheet Assembly
For the werkvoorbereiders' most time-consuming job — putting an A0 together — the plugin can now
build and check a sheet from the conversation.
- **`section`** — a section view along a line (mm) or straight across a wall (`section <wallId>`),
  with depth, vertical range (auto from the model elements it cuts), name, scale and the office
  pack's section template/phase. Reports which compass direction it looks and the in-view x range the
  annotation commands use (position along the view, stable when the view is cropped later).
- **`crop`** — crop a view to its model elements (`--fit`: the largest cluster, so scattered context
  far away is dropped; DWG underlays / point clouds / links ignored; `--elements` to choose), to a
  rectangle (`--box`), or `--off`; 3D views get a section box and are cropped to it. Without it an
  uncropped plan over a BGT underlay is metres wide on paper and fits no cell.
- **`viewport`** — put one view at a paper position (`--at x y`) or several in a grid (`--grid CxR`,
  optionally inside `--area`), with `--scale`, `--margin`, `--reserve` (stamp strip) and `--title off`.
  Every placement reports its paper outline; a view that does not fit its cell is still placed with a
  `WARN` and the scale that would fit; a view already on another sheet is refused with that sheet's
  number and its cell stays empty. Schedules too. `--move` / `--remove` for corrections.
- **`dimension`** — linear dimensions in the active plan/section/elevation through element ids (outer
  faces, grids, levels, reference planes, family centre planes), between two points (`--points`,
  nearest face/grid within 100 mm), or a vertical level string (`--levels`). Reports every segment in
  mm and the references used; Revit's refusal comes back with the likely cause. Coordinates go through
  `--points` rather than four bare numbers so they can never be mistaken for element ids.
- **`text`** — text notes (`\n` line breaks, `--size` in paper mm creating a type when none matches,
  `--leader`). The building block for annotating from a specification, which stays chat work.
- **`sheetinfo`** — the sensor: paper size, title block, stamp fields (office pack list or the title
  block's own text parameters; patterns like `NL_META_*` and `X_1..5` expand, filled fields are listed
  and empty ones counted), viewports with paper outlines, text and schedules on the sheet, and overlap /
  off-paper checks (a ≤ 30 mm overlap strip is INFO, anything bigger WARN — the crop-margin overlaps on a
  finished Office A0 were 10–27 mm strips).
- **Extended:** `sheet --number --name --set "Param=Value"` (sheet or title-block field); `tag --leader
  --at x y`; `screenshot --view <id|name|sheet number>` (sheets were refused before; now exported, and
  without having to activate them); `list dimensions|text` with values; `list sheets` with view counts;
  `activate <sheet number>`.
- Arg parsing (including refused flag combinations), layout maths (cells, fit, suggested scale,
  overlaps), the `crop --fit` clustering, stamp patterns, the section frame and dimension bookkeeping
  are Revit-free and covered by headless tests.
### Added — Plan 10: Project Folder Context
- **Project folder per model.** The chat now knows the folder with this model's reference
  documents (bestek, memo's, DO-tekening, inspection reports). Default: the folder the `.rvt`
  is in; Settings → *Project folder* (Browse… / Clear) overrides it for the current project only
  (`ProjectContextFolders` in settings.json, keyed like the attachment store). A configured
  folder that no longer exists falls back to the model folder; an unsaved or cloud model has no
  default. Every output names the folder with its source: `(map van het model)` / `(ingesteld)`.
- **`docs [subfolder]`** — manifest of the folder, one level deep: relative names, kind tags,
  sizes, capped at 200 entries. Revit's own files (`.rvt`/`.rfa`, numbered backups, `_backup`
  and point-cloud `… Support` folders) are not listed.
- **`readdoc <name>`** — one document, name case-insensitive (a unique prefix is enough,
  ambiguous names list the matches): text inline (400k-char attachment cap), PDFs as text via
  the plan-11 PDF helper (cached by content hash; a slow extraction continues in the background),
  Word `.docx` as paragraph text, images/other as the absolute path plus a hint. Only names
  inside the folder resolve: `..`, absolute paths, prefix siblings and links out are refused.
- **Per-turn context.** All backends get a bounded `PROJECT FOLDER` block (the same manifest,
  ≤100 entries) with "use readdoc <name> when relevant; don't guess at their contents"; the
  Claude Code backend also gets `--add-dir <folder>` so it can read the files natively.

### Added — Plan 11: Office Standards
- **Standards packs.** An office's Revit standard as data: `pack.json` + an always-on `core.md`
  + on-demand topic sheets + `checks.json`. Packs are found via the new `StandardsPackPath`
  setting (a network share is mirrored locally in the background and only the mirror is read
  on Revit's thread), `%LOCALAPPDATA%\VibeModel\standards\`, or the add-in folder;
  a pack is active for a document only when its fingerprint (phases, scope box, shared
  parameter — measured names, aliases for alternative spellings) matches. No hardcoded office
  names in code; the repo ships only a fictional `standards/example-office` sample.
- **`template`** — live inventory of how the document was set up (phases + filters, view
  templates grouped by prefix, scope boxes, coordinates and pins, CAD links vs imports, family
  prefix classes, sheets, title blocks) and which pack matches.
- **`standards`** — active pack overview and search diagnostics, `standards <topic>`, `core`,
  `--refresh`, `--prompt` (exactly what the chat receives), `--pack <id>`; plus the office's own source documents: `--index` (PDF/txt split into numbered
  chapters, cached by file hash, PDF text via an out-of-process helper), `docs`,
  `standards <doc> <chapter>`, and an explicit `distill` that turns the manual's rule chapters
  (pack `distill.sources`: documents + chapters, most important first) into `core.generated.md`
  (used when the pack says `"core": "generated"`). Cached indexes are keyed on file content and
  splitter version, so improved chapter detection re-splits them.
- **Prompt injection.** Every chat turn (all three backends) gets a `TEMPLATE:` block (≤1 kB)
  and, with an active pack, a `STANDARDS:` block with the core rules (Claude Code: in the per-turn
  appended system prompt). Rendered on Idling on the Revit thread (at most every 3 s after
  edits), read lock-free by the chat backends.
- **Default phase on creation.** `wall`, `floor`, `place`, `placeid`, `room` take
  `--phase existing|new|temp|demolish|future` and, with an active pack, default to the pack's
  phase (new work → e.g. `2. New construction` instead of the active view's existing phase),
  warning when the element will not show in the active view (rooms: Revit does not allow
  re-phasing them; an explicit `--phase` reports "not applicable"). A phase Revit refuses leaves
  the element's previous phases intact and is reported. New **`phase`** command re-phases
  existing elements from the office table.
- **Office defaults** for `view` (view template, phase, phase filter, browser-folder parameters;
  `--template`/`--no-template`, plus `--name` and `--activate`) and `sheet` (default title block,
  stamp-field hint). New **`activate <viewId|name>`** opens a view — the AI could not switch views
  through `exec`, so new work on the office phase stayed invisible in the template's work views.
- **`check`** — audit (OK/WARN/FAIL/SKIPPED per rule, Fix lines, Verdict) against the pack's
  rules (13 rule kinds: pins, phases, phase filter digits, plot-view templates, family prefixes, required parameters, survey/base
  point, file name, schedule phase, links vs imports), `check selected|<ids>`, `check --stats`.
  After every modifying command/batch the element-level rules run on what was just created and
  add one `-- check: …` line (JSON: additive `check` object). Hits are logged (rule id + count).
- **`library` / `loadfamily`** — background-built, cached index of the office family library
  (an offline share is retried at most every 5 min; `library --rebuild` forces it);
  `place` suggests `loadfamily "<path>"` when a family is not loaded.
- **`linkcad`** (link, never import; off-axis correction and orient-to-view off; shared
  coordinates or centre-to-centre for the first underlay; pinned; refuses a DGN until a base map
  is linked and shared coordinates exist, `--force` overrides; `--dgnview <model view>`, default
  `Default`; an unreadable file is an error, not a link with id -1) and **`coords`** (survey point, base point, rounding, shared coordinates, pins).
- Optional office additions file (`additionsFile`) appended to the core, next to the pack or in
  the source-documents folder (cached locally by the background index run).
- Settings dialog: *Office standards* section (enable switch, pack folder, source-documents folder).
- New helper `VibeModel.PdfText.exe` (PdfPig) for PDF text, built with the add-in and deployed to
  its own `VibeModel.PdfText\` subfolder by `build.ps1` and `install.ps1`.

## v1.2.5 — Chat Pane Loopback Fix (2026-09-03)

### Fixed
- **In-process HTTP calls now dial `127.0.0.1` instead of `localhost`.** The chat pane's
  pre-flight probe (`ClaudeCodeBackend.IsServerHealthy`), the per-turn `/context` gather and
  tool calls (`ChatBackendBase`), the document-title lookup for per-project attachments
  (`ChatPane.GetProjectKey`), and the system prompt/port directive handed to the Claude CLI
  all addressed the embedded server as `localhost`. `RevitHttpServer` binds
  `IPAddress.Loopback` (IPv4 only), but Windows resolves `localhost` to `::1` first; where
  that IPv6 attempt is dropped rather than refused (observed with corporate endpoint
  protection), each request burned its full timeout. For the 2 s pre-flight that surfaced as
  *"VibeModel's HTTP server isn't responding on port 18884"* and a chat pane that refused to
  send — while the same server answered an IPv4 request in ~4 ms. Every in-process caller now
  builds its URL from one helper (`RevitHttpServer.BaseUrl(port)`, over the documented
  `LoopbackHost` constant) so the host can't drift back at a single call site.
## v1.2.3 — Plan 09: Seed Pack Design (2026-07-21)

### Docs
- **Plan 09 — Seed Pack** (`docs/plans/09-seed-pack.md`, proposed, nothing built yet): design
  for the distribution half of the memory feature — how a curated, impersonal `global.jsonl`
  gets exported by users, curated by the maintainer (a new offline `scripts/curate_memory.py`
  that dedupes, flags personal content, expires version-specific facts, and re-stamps entries
  with stable ids), shipped in the repo at `seed/global-memory.jsonl` + a versioned
  `seed-manifest.json` deployed next to the DLL, and inherited by new users on first run.
  Updates merge into an existing local `global.jsonl` idempotently via stable entry ids, a
  monotonic `seedVersion` gate, and tombstone respect so user-deleted facts never resurrect.
  Personal `profile.jsonl` never enters the pipeline — curation is the only human-reviewed door.

## v1.2.2 — Plan 08: Profiles & Memory Design (2026-07-21)

### Docs
- **Plan 08 — Profiles & Memory** (`docs/plans/08-profiles-and-memory.md`, proposed, nothing
  built yet): design for a per-user profile + shareable global capability map, captured via a
  new LLM-called `remember` command (phase 2: automatic failure→workaround candidates), stored
  as tombstoned JSONL under `%LOCALAPPDATA%\VibeModel\memory\`, and injected fresh each turn
  into all three chat backends' system prompts. Seed-pack export/inherit is deferred to plan 09.
### Added
- **Chat history persistence + browser** — conversations are no longer lost on "New Chat".
  Every chat is saved to `%LOCALAPPDATA%\VibeModel\chats\` (one JSON file per session, written
  after each completed reply, capped at 200 sessions) with a title derived from the first user
  message, the Revit project name, and the active backend. A new **History** button in the chat
  pane header opens a dark-themed browser — sessions grouped by project (most recent first),
  searchable by title/project, with per-row open/delete and double-click to open. Opening a
  session restores the full transcript and rebuilds conversation context so you continue where
  you left off (Anthropic API and Local LLM backends replay the transcript into the model;
  the Claude CLI backend shows the transcript and starts a fresh CLI session, clearly labeled).
  Store and replay logic are UI-free and covered by 13 new headless tests (round-trip, title
  truncation, empty-session rule, prune-over-cap, corrupt-file tolerance, and the transcript
  replay rules: system messages skipped, same-role turns merged, trailing user turn padded).
### Added
- **Model-state stamp (change awareness)** — every HTTP command response now reports whether the
  user changed the model since the AI's previous command, so stale-context confusion can't happen
  silently. A `DocumentChanged` subscription counts committed transactions per document and
  classifies them by the shared `VibeModel: ` transaction-name prefix (now enforced centrally in
  `TransactionHelper`); manual edits, undo and redo count as USER edits. Text responses gain one
  trailing line (`-- model #47 | view: {3D} | selected: 0`, with an explicit warning when user
  edits occurred); JSON envelopes gain an additive `meta` object (`edits`,
  `userEditsSinceLastCommand`, `activeView`, `selectedCount`). Stamping is centralized in the
  command handler so no command can forget it; `/health` and `screenshot` stay untouched, a batch
  gets one stamp, and the user-edit counter resets only when a stamp is actually delivered.
  Tracker + stamp rendering are Revit-free (`ModelChangeTracker`) and covered by 20 headless tests.
  Tracker state deliberately survives document close — Revit's closing event fires before the
  close can still be canceled, so pruning there could silently wipe real user-edit counts.

## v1.2.1 — Source-Data Reading & Seamless Chat Port Isolation (2026-07-20)

### Added
- **Source-data reading (DWG + point clouds)** — six new read-only commands so the AI reads
  imported source data as actual vectors/points instead of guessing from screenshots:
  `imports` (CAD imports/links with mm bbox + curve/layer counts), `layers <id>` (DWG layer
  names with object counts), `curves <id> <layer> [--bbox] [--closed] [--page]` (real
  coordinates in mm — `--closed` chains segments into footprint loops paste-ready for
  `/floor`), `pointclouds` (loaded `.rcp`/`.rcs` instances with mm bbox), `pcprobe <id> <x>
  <y> [r]` (height histogram + robust ground estimate at a point), and `pcline <id> <x1> <y1>
  <x2> <y2> [step]` (ground profile along a line). Ground estimation picks the lowest DENSE
  band of points and averages only its bottom 200mm, so parked cars, trees and bins above the
  surface — and a facade rising from the street — don't skew it; `pcline` additionally
  rejects per-station spikes and interpolates occluded stations (flagged `interp`).
  Point-cloud filter coordinate space is API-ambiguous, so the helper self-probes filter
  space × orientation combos, only accepts a strategy whose points survive a sanity check
  against the probe column, and caches the winner. Pure algorithms (curve chaining,
  point-cloud analysis, arg parsing) are Revit-free and covered by 37 new headless tests.

### Fixed
- **Multi-instance chat port poisoning** — the Claude Code chat backend wrote one shared
  `system-prompt.txt` once at startup; a second Revit instance (fallback port 18885+) would
  overwrite it with its own port and leave it stale after closing, breaking chat in the surviving
  instance with connection-refused curls. The prompt file is now per-port
  (`system-prompt-<port>.txt`) and rewritten before every message; the legacy shared file is
  deleted at startup.

### Added
- **Self-healing port directive** — every chat turn's appended system prompt names the live port
  as authoritative, so resumed sessions ignore dead ports remembered from earlier conversation
  history (`ClaudeCodeBackend.BuildPortDirective`).
- **Pre-flight health check** — the chat backend probes `/health` (2s timeout) before spawning the
  CLI; a down server yields one plain-language error instead of minutes of failed curl attempts.
- **Server discovery files** — `RevitHttpServer` publishes
  `%LOCALAPPDATA%\VibeModel\servers\<port>.json` (`{port, pid, startedUtc}`) on start and deletes
  it on stop (new `ServerDiscovery` helper), so external clients find fallback ports instead of
  assuming 18884; stale files are detectable via their dead PID. Documented in CLAUDE.md + README.
- **Port-isolation tests** — 8 new xUnit tests (`PortIsolationTests`) covering per-port prompt
  paths/content, the auth block, the port directive, and discovery JSON round-trip (suite: 107).

### Build
- **Add-in deployment is now opt-in** — plain `dotnet build` / `dotnet test` compile only;
  deploying into the live Revit addins folder requires `-p:Deploy=true` (build.ps1 passes it
  after closing Revit). Worktree builds (bot/CI sandboxes under `.worktrees`) additionally
  default to SkipDeploy, so branch code can never overwrite the installed add-in or hit
  locked-file MSB3027 failures by accident.

## v1.2.0 — Vision, Structured I/O & Modeling Breadth (2026-07-11)

### Added
- **Modeling breadth** — six new creation commands, each a thin wrapper over a stable Revit
  factory API following the `WallCommand` template: `grid`, `level`, `view` (floor plan + 3D),
  `room`, `sheet` (optional title block + placed view), and `tag`. All are born structured
  (`IStructuredCommand` — JSON `data` with the created element ID + name, structured errors with
  codes), and use `TransactionHelper` + the `IModificationCommand` marker so they assimilate into
  the batch undo group automatically. `tag` resolves the element→tag category via an explicit map
  and degrades gracefully (`TYPE_NOT_LOADED`) when no tag family is loaded.
- **Structured I/O (foundation)** — commands can return JSON via `?format=json` or an explicit
  `Accept: application/json` header (curl's default `*/*` stays text). Success envelope
  `{"ok":true,"data":{...}}` (or `{"ok":true,"text":"..."}` for unmigrated commands); failure
  `{"ok":false,"error":{"code","message","suggestion"}}`. Text mode is byte-identical except
  errors gain an additive `Hint:` line. New `CommandResult` type + optional `IStructuredCommand`
  interface (per-command migration). Registry now runs every command through one `ExecuteCore`
  returning a structured result; the batch path decides Assimilate vs RollBack on `.Success`
  instead of string-sniffing `"ERROR"`. Query commands migrated to structured output:
  `info`, `list`, `selected`, `get`, `params`, `levels`, `views` — each emits structured `data`
  in JSON mode (byte-identical text in text mode) and precise error codes (`BAD_ARGS`,
  `ELEMENT_NOT_FOUND`, `NO_SELECTION`, `NO_ACTIVE_VIEW`, …).
- `POST /batch?format=json` returns a structured envelope:
  `{"ok":<all-ok>,"atomic":bool,"rolledBack":bool,"results":[{command,args,result}]}`
  (default batch output stays text).
- The Anthropic and Local LLM chat backends now request JSON for tool calls (`&format=json`,
  except `screenshot`), so the model reads structured `{ok,data}` / `{ok,error}` and self-corrects
  from the error `suggestion` instead of scraping text.
- **Vision** — new `/screenshot` command exports the active view to a PNG (default 1536px long
  edge, `screenshot [pixels]`) and returns its path. `AnthropicDirectBackend` reads the file and
  inlines it as an image block so the model can SEE the model; both Anthropic and Claude-CLI
  backends are nudged to screenshot **autonomously** (after visible changes / layout questions,
  not for pure data queries). Only the most recent screenshot is kept as an image in history
  (older ones downgrade to text) to avoid re-shipping base64 every turn; old temp PNGs are pruned
  to the last 20. Local LLM backend gets the path as text only (most local models aren't vision-capable).
- `POST /batch` now runs as **one undo unit** — the whole batch wraps in a single Revit
  `TransactionGroup` and collapses to one Ctrl+Z (undo label e.g. `"VibeModel: 4× wall"`).
  Resilient by default (a mid-batch failure keeps the commands that succeeded and reports the
  per-command `ERROR`); opt-in atomic mode (`#atomic` first line or `?atomic=1`) rolls back the
  entire batch on any failure. Read-only batches create no undo entry.
- Combined `/context` endpoint composing `info` + `activeview` + `selected` in one call.
- Friendly status lines streamed during chat tool calls (`StatusVerbs`) — "Creating wall…",
  "Looking at the model…", etc.
- **Optional shared-secret auth gate** — set `VIBEMODEL_TOKEN` before launching Revit and every
  request except `/health` must carry a matching `X-VibeModel-Token` header; missing/wrong token
  → `401` (`unauthorized` error envelope in JSON, `ERROR: Missing or invalid token.` in text).
  Off by default (unset ⇒ server stays fully open on loopback, unchanged). Token compared in
  fixed time. The in-app chat backends read the same env var and add the header automatically, so
  enabling it doesn't break in-Revit chat.
- **Headless unit test suite** — first automated tests for VibeModel (`VibeModel.Tests`, net48,
  no Revit required): batch parsing, command registry, `CommandResult` rendering, and query
  arg-parsing. Batch parsing extracted into a standalone testable `BatchParser`; the add-in
  exposes internals via `InternalsVisibleTo`, and a `SkipDeploy=true` MSBuild switch lets tests
  build without the Revit-locked add-in DLL.

### Changed
- Chat backends gather model context via a single `/context` round-trip instead of three serial
  HTTP calls, each of which crossed the ExternalEvent boundary. Measured ~2–3× faster
  steady-state and ~2.2s→95ms on the cold first turn of a session (lower time-to-first-token).
- Batch is now handled as a single main-thread request (`EnqueueBatchAndWait` → `ExecuteBatch`)
  rather than a blocking `EnqueueAndWait` per line, with a batch-size-scaled timeout
  (`max(30s, 5s + 2s×N)`, capped at 5 min) and a matching socket send-timeout.
- Chat tool-activity labels now appear **before** the tool runs (was after) and use a friendly
  verb instead of the raw `[toolName]`; the streaming placeholder shows "Looking at the model…"
  the instant a message is sent. Status lines are UI-only — never added to conversation history.
- **Chat backends share a `ChatBackendBase`** — the duplicated plumbing (single-call `/context`
  gathering, HTTP tool execution, background-send guard, CTS lifecycle, and the system-prompt
  scaffold) is pulled up into one abstract base; the agentic loops stay per-backend (SSE
  streaming, NDJSON, child process, vision inlining). `ClaudeCodeBackend` now uses the same
  single `/context` round-trip as the others — it previously made three separate serial calls
  (`/info` + `/activeview` + `/selected`). ~490 lines of duplicated code collapse into the base.

### Fixed
- **Chat streaming no longer janks on long responses** — the streaming pane rebuilt the entire
  response string and reassigned the `TextBlock` on every token (O(n²) on multi-KB answers).
  Tokens now accrue into a buffer that a `DispatcherTimer` flushes at ~25fps, so a long answer
  renders in O(length) total; the final markdown render on completion is unchanged.

## [v1.1.4] — Chat Session Continuity (2026-05-05)

### Fixed
- Chat session no longer goes amnesiac when the Claude CLI subprocess is killed mid-task — `ClaudeCodeBackend.cs` now captures `session_id` from the first `system` init NDJSON event instead of waiting for the terminal `result` event, so a follow-up like "ja?" continues the prior conversation via `--resume` rather than starting fresh
- Replaced 2-min absolute process timeout with a 3-min idle timeout that resets on every NDJSON line — multi-step modeling tasks with continuous tool calls are no longer cut off prematurely
- Idle kills now produce a visible chat-bubble notice (via both `onToken` and `fullResponse`) instead of silent termination; mid-tool kills also clear `_sessionId` to prevent broken `--resume` transcripts (orphan `tool_use` without matching `tool_result`)
- Stale `--resume` failures (e.g., CLI session store rotated) auto-retry once as a fresh chat with a `[Resumed session was stale — starting fresh chat.]` prefix instead of silently dying

### Changed
- Clarified `assistant`-block iteration in `ProcessNdjsonLine` by separating `currentBlockType` (per-iteration) from `lastBlockType` (post-loop), removing a dual-use variable that could reuse a stale type when a block lacked a `type` field

## [v1.1.3] — Upgrade Recommended Model to Qwen 3.5 9B (2026-03-27)

### Changed
- Recommended local LLM model updated from Qwen 2.5 7B to Qwen 3.5 9B across docs and UI
- Model table in `LOCAL_LLM_SETUP.md` now lists Qwen 3.5 family (0.8B, 4B, 9B)
- Hardware requirements table updated for new model sizes
- Settings dialog tool-use hint updated to reference Qwen 3.5 9B+

## v1.1.2 — Fix Local LLM Empty Response (2026-03-24)

### Fixed
- Local LLM backend returned empty responses — `JavaScriptSerializer` deserializes JSON arrays as `ArrayList`, not `object[]`, causing silent cast failures in stream parsing
- Same latent bug fixed in Claude Code backend (`ClaudeCodeBackend.cs`) and Settings "Test Connection" (`SettingsDialog.cs`)

## v1.1.1 — Fix DLL Blocked by Windows (2026-03-23)

### Fixed
- Installer now automatically unblocks DLLs downloaded from the internet (`Unblock-File`) — prevents "External Tool Failure" / HRESULT: 0x80131515 on first load
- Added DLL unblock step to manual installation instructions in README
- Added troubleshooting entry for FileLoadException / blocked DLL error

## v1.1.0 — Local LLM Support (2026-03-21)

### Added
- Local LLM backend (`LocalLlmBackend`) — connects to any OpenAI-compatible server (llama.cpp, Ollama, LM Studio)
- Shared `ToolDefinitionBuilder` for generating tool definitions in both Anthropic and OpenAI formats
- Settings UI redesigned with tabbed backend selector (Claude CLI / Anthropic API / Local LLM)
- Local LLM settings: endpoint URL, model name, tool use toggle, response timeout slider (30-300s)
- Test Connection button for validating local LLM server reachability
- Chat-only info banner when local LLM is connected without tool use enabled
- Backend status indicator in chat header showing active backend type
- Backend hot-switch via Settings with automatic session reset
- Setup guide (`docs/LOCAL_LLM_SETUP.md`) covering llama.cpp, Ollama, LM Studio, recommended models, and troubleshooting

### Changed
- Backend selection logic now supports `auto`, `claude-cli`, `anthropic-api`, and `local-llm` modes
- `AnthropicDirectBackend` refactored to use shared `ToolDefinitionBuilder`
- Settings dialog now saves all backend settings on save (not just active tab)
- Default preferred backend changed from `direct` to `auto`
- Welcome message updated to mention local LLM option
