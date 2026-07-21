# Plan 09 — Seed Pack: Shipping Curated Global Memory

> Goal: give every new VibeModel user a head start by shipping a curated, impersonal
> "starter memory" in the repo — the accumulated capability knowledge of prior users — and
> have it inherit cleanly on first run and update safely thereafter, without ever letting one
> user's personal `profile.jsonl` leak into the repo. This is the distribution half of the
> memory feature (plan 08); plan 08 builds the store, plan 09 builds the pipeline that fills it.

> **STATUS (proposed).** Nothing here is built. Depends on plan 08 (`MemoryStore` + the JSONL
> schema, `docs/plans/08-profiles-and-memory.md`) landing first — this plan consumes that
> contract and does not redefine it. Unrelated to plan 06 (usage export), which was **never
> built**; §1 notes the one optional touch-point.

## TL;DR

New users start with an **empty** memory, so the first weeks of every install rediscover the
same lessons (`place` fails for a hosted family without a host → select the host and use
`placeid`; point-cloud ground is the lowest dense band). The seed pack fixes that: a
maintainer-curated `seed/global-memory.jsonl` ships **inside the addin payload**, and
`MemoryStore` seeds a new user's local `global.jsonl` from it on first run.

Four properties make this safe rather than a footgun:

1. **Only the impersonal slice moves.** Personal memory (`profile.jsonl`) is structurally
   excluded from every path in this plan — export reads `global.jsonl` and nothing else, and the
   curation script hard-refuses any personal-looking entry (§5). The repo is the only door in,
   and a human walks through it.
2. **Ids are stable, so merges are idempotent.** Each seed entry keeps its `id` (the plan 08 §1
   entry field) across seed versions. Re-applying a seed can never duplicate a fact.
3. **Tombstones are respected.** If a user deletes a seeded fact, plan 08's store appends a
   `deleted:true` line with the same `id`; a later seed update that still contains that `id` is
   **skipped**, so deletes never resurrect.
4. **A version stamp gates re-merge.** The seed carries a monotonic `seedVersion`; the local
   store remembers the last one it applied and only merges when the shipped seed is newer.

The whole pipeline is **Export → Curate → Ship → Inherit**, with a privacy gate wrapped around it.

---

## 1. Export — user hands their `global.jsonl` to the maintainer

The impersonal slice already lives at a known, documented path:
`%LOCALAPPDATA%\VibeModel\memory\global.jsonl`. Plan 08 §4 already gives the user a
`memory export` command that **prints that folder path + file list** — the plain JSONL files are
attachable as-is. So the simplest viable "share" is **already covered by plan 08 plus one line of
docs** — tell the user to attach that one file. That's the MVP and it costs nothing new here.

Two optional conveniences, in ascending effort:

- **Extend plan 08's `memory export`** (or add a sibling `memory export-file`) to **copy only
  `global.jsonl`** to a user-picked / Desktop path named `VibeModel-memory-<date>.jsonl` and
  return the full path, so the user doesn't have to dig through `%LOCALAPPDATA%`. It must never
  read `profile.jsonl` — the source path is hard-coded to the global file (§5). Because plan 08's
  `MemoryCommand` is reflection-discovered via `ClaudeCommandRegistry`, the same class is
  simultaneously an in-app chat tool and a `/memory` curl endpoint — no extra wiring.
- **A Settings button** ("Export shareable memory…") calling the same code, for non-CLI users
  (sits next to plan 08 §4's "Clear memory…" button in `SettingsDialog`).

**Bundling with plan 06:** if the usage-export zip is ever built, `global.jsonl` should ride along
inside it (it's already impersonal), so one "share" action covers both. But plan 06 is **not
built**, so this plan does **not** depend on it — export here stands alone as a single-file copy.

The maintainer collects these files however is convenient (email, Discord attachment) into a local
`contributions/` folder that is **git-ignored** and never committed — raw contributions are inputs
to curation, not artifacts.

---

## 2. Curate — the maintainer's pass before anything ships

This is the human gate and the heart of the plan. Tooling assists; the maintainer decides. Propose
**`scripts/curate_memory.py`** (pure offline Python, no addin code — mirrors the plan 06 §7
`analyze_usage.py` posture: runs over collected files, changes nothing in the add-in).

**Input:** one or more contributed `global.jsonl` files (a folder).
**Output:** a proposed `seed/global-memory.jsonl` + a human-readable report (`curation-report.md`)
the maintainer reviews and hand-edits before committing.

The script's automated passes:

- **Drop tombstones and deleted entries.** Anything with `deleted:true` is removed outright — the
  seed only carries live facts. (Tombstone *semantics* matter on the inherit side, §4, not here.)
- **Dedupe by normalized text.** Lowercase, collapse whitespace, strip trailing punctuation; group
  near-identical facts. Where several users learned the same thing, keep one and prefer the entry
  with the richest `why` and `verified:true` (both plan 08 §1 fields).
- **Flag personal / project-identifying content for the maintainer.** Regex + heuristics surface
  entries mentioning file paths, project names, client names, addresses, emails, `exec` code
  bodies, or anything that reads like a specific model rather than a general capability. These are
  **flagged, not auto-published** — the maintainer strips or rewrites them. (Auto-*reject* the
  obvious secrets: API-key / token shapes, long hex/base64 — reusing the plan 06 §5 / plan 08 §5
  redaction idea.)
- **Surface conflicts.** When two facts contradict (e.g. two different claimed default screenshot
  sizes), the report pairs them for a human ruling rather than silently picking one.
- **Expire version-specific facts.** Any entry whose `revit` field is set to a version no longer
  in the supported set (`build.ps1` `ValidateSet` today: 2022–2025) is dropped or flagged.
  Version-neutral facts (`revit:null`) always survive this pass.
- **Re-stamp for shipping.** Every surviving entry gets `source:"seed"` (a valid plan 08 §1
  `source` value) and `verified:true` — a fact only enters the seed once a human has verified it
  (criteria below). Its `id` is **preserved** if it already had a stable one, otherwise assigned a
  deterministic id derived from normalized text (a short hash) so the *same fact* re-curated later
  keeps the *same id* — critical for idempotent merges (§4).

**Acceptance criteria for a fact entering the seed** (documented at the top of the script and here):

- **Impersonal** — describes VibeModel/Revit capability, not any specific model, user, or project.
- **General** — true across models and users, not a one-off workaround for one file.
- **Verified** — the maintainer confirmed it still holds against a current build (or it's
  self-evidently a schema/usage fact).
- **Current** — not tied to an unsupported Revit version.
- **Has a `why`** — a bare assertion with no "what taught us this" is low-value; prefer entries
  that explain the lesson (the plan 08 §1 `why` field exists precisely so facts can be re-verified
  or expired instead of trusted forever).
- **Non-duplicative** — not already covered by another seed entry.

The script is **advisory**: it produces a candidate file + the report, and the maintainer edits
`seed/global-memory.jsonl` directly before committing. Curation is a git commit reviewed like any
other — that review **is** the privacy gate (§5).

---

## 3. Ship — where the seed lives and how it deploys

- **In the repo:** `seed/global-memory.jsonl` (top-level `seed/` folder, next to `src/`). Plus a
  stamp file `seed/seed-manifest.json`:

  ```json
  { "seedVersion": 1, "entryCount": 12, "generatedUtc": "2026-07-21T00:00:00Z",
    "minAddinVersion": "1.3.0" }
  ```

  `seedVersion` is a **monotonic integer** bumped every time the seed content changes — it is the
  key the inherit step gates on (§4), not the addin version. The maintainer bumps it in the
  curation commit.

- **Into the addin payload:** extend the `DeployAddin` target in `VibeModel.csproj` (the
  `AfterTargets="Build"` target that today copies `VibeModel.dll`, `Markdig.dll`, and
  `VibeModel.addin` into `$(RevitAddinFolder)`) with two more `<Copy>` items that place
  `global-memory.jsonl` and `seed-manifest.json` next to `VibeModel.dll`. So the shipped location
  the addin reads at runtime is `<AssemblyDir>\global-memory.jsonl` — resolved relative to the
  executing assembly, the same way the addin already finds its own files.

  ```xml
  <!-- Ship the curated memory seed next to the DLL -->
  <Copy SourceFiles="$(ProjectDir)..\..\seed\global-memory.jsonl"
        DestinationFolder="$(RevitAddinFolder)" SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)..\..\seed\global-memory.jsonl')" />
  <Copy SourceFiles="$(ProjectDir)..\..\seed\seed-manifest.json"
        DestinationFolder="$(RevitAddinFolder)" SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)..\..\seed\seed-manifest.json')" />
  ```

  `Condition="Exists(...)"` keeps builds green before the seed folder exists. The copies inherit
  the existing opt-in `Deploy=true` / `SkipDeploy` gating for free (the whole `DeployAddin` target
  only runs under `Deploy=true`, and worktree/CI builds default `SkipDeploy=true`), so sandbox
  builds still never touch the live Addins folder.

- **Alternative considered — embed as a resource.** The ribbon icons are embedded resources in
  `VibeModel.csproj` (`<EmbeddedResource>` for `chat-32.png` / `chat-16.png`), so the seed *could*
  be embedded in the DLL. Rejected: a loose file is inspectable/editable by the user and by us,
  easier to diff in PRs, and matches "memory is files on disk." Embedding also forces a rebuild to
  re-seed; a loose file doesn't.

---

## 4. Inherit — first run and safe updates

`MemoryStore` (plan 08, `src/VibeModel/Infrastructure/MemoryStore.cs`) gains a
**seed-reconciliation step** it runs once at load, before it renders memory into the chat prompt
(`RenderForPrompt()`). Let `shippedSeed = <AssemblyDir>\global-memory.jsonl`,
`localGlobal = %LOCALAPPDATA%\VibeModel\memory\global.jsonl`, and `appliedSeedVersion` = a small
marker the store persists (a `memory\seed-state.json` `{ "appliedSeedVersion": N }`, or a key in
`settings.json` via `SettingsManager`).

**First run (local `global.jsonl` absent):**
Copy every live entry from the shipped seed into a fresh `localGlobal` via `Append("global", …)`,
then set `appliedSeedVersion = shipped seedVersion`. Simple, whole-file seed.

**Update (local exists, shipped `seedVersion` > `appliedSeedVersion`):** merge, id-by-id:

- Build the set of **ids the user has already seen** = every `id` present in `localGlobal`,
  including tombstones (`deleted:true`). Plan 08's store already loads the file taking the last
  line per id, so both live and tombstoned ids are cheaply enumerable.
- For each **live** entry in the shipped seed:
  - if its `id` is **not** in the seen set → **`Append`** it (a genuinely new seeded fact).
  - if its `id` **is** present as a **tombstone** → **skip** (user deleted it; never resurrect).
  - if its `id` **is** present as a **live** entry → **skip** (already have it; don't duplicate,
    and don't clobber any edit the user made).
- After the pass, set `appliedSeedVersion = shipped seedVersion`.

Because plan 08's store is **append-only**, "append if unseen" is the only write; nothing is
rewritten, so a torn write costs at most one entry and the next launch re-tries. Because ids are
**stable** across seed versions (§2 re-stamp), an id seen once is seen forever — the merge is
idempotent and convergent. Because tombstones share the deleted fact's id, a delete is permanent
against all future seeds.

**Why version-gate at all** (vs. always scanning the seed)? It makes the common path — no seed
change since last launch — a single integer compare, no file scan, and gives a clean hook for a
"seed changed, N new facts added" log line later.

**Edge cases the reconciler must handle:** shipped seed missing (skip silently — not every build
ships one); malformed seed line (skip that line, log, continue — same swallow-all discipline plan
08 already applies to corrupt lines); `appliedSeedVersion` marker missing but `localGlobal` present
(treat as version 0 → a full merge pass runs once, which is safe because it's idempotent).

---

## 5. Privacy gate — the one hard rule

**`profile.jsonl` never leaves the machine through this pipeline. Ever.** This restates and
enforces plan 08 §5's boundary, structurally rather than by convention:

- **Export reads one file.** The `memory export` copy (§1) and the Settings button reference the
  `global.jsonl` path as a constant; there is no code path in the export that opens
  `profile.jsonl`. A headless test asserts the exporter never touches the profile file.
- **Curation refuses personal entries.** `curate_memory.py` treats a `user`-scope / personal line,
  or any entry failing the impersonal heuristic (§2), as a hard reject with a loud warning — it
  can't reach the output file without the maintainer overriding by hand.
- **The repo is the only inbound door, and it's human-reviewed.** Nothing auto-publishes; the seed
  enters via a normal git commit the maintainer reads. If personal content ever reaches
  `seed/global-memory.jsonl`, it's a human review miss, not a silent leak — and the commit history
  makes it auditable and revertible.
- **Documented** in `CLAUDE.md` and README (alongside plan 08's memory docs): which file is
  shareable (`global.jsonl`), which never is (`profile.jsonl`), and that the seed you inherit was
  human-curated.

---

## 6. Phasing

**Phase 1 — Inherit (consumes plan 08, unblocks everything).**
Seed reconciliation in `MemoryStore` (first-run copy + versioned idempotent merge + tombstone
respect + `appliedSeedVersion` marker). Ship an initial `seed/global-memory.jsonl` (even a tiny
hand-written one) + `seed-manifest.json`; wire the two `DeployAddin` copies. Unit-test the merge
logic headlessly (pure, no Revit). This is the load-bearing half.

**Phase 2 — Curate (maintainer tooling).**
`scripts/curate_memory.py`: dedupe, personal-content flagging, conflict surfacing, version
expiry, deterministic id assignment, report generation. Offline Python, unit-testable.

**Phase 3 — Export convenience (optional).**
Extend plan 08's `memory export` to the global-only file-copy + Settings button. Optional; plan
08's path-printing `memory export` already suffices as the MVP. Fold `global.jsonl` into plan 06's
zip **iff** that ever ships.

---

## 7. Files touched

**New**
- `seed/global-memory.jsonl` — the curated, shipped seed (starts small, grows via curation).
- `seed/seed-manifest.json` — `seedVersion` + counts + stamp.
- `scripts/curate_memory.py` — maintainer curation pass (phase 2).
- `src/VibeModel.Tests/MemorySeedTests.cs` — first-run seed, idempotent re-merge, tombstone
  respect, version-gate, malformed-line tolerance.

**Modified**
- `src/VibeModel/Infrastructure/MemoryStore.cs` (plan 08) — add the seed-reconciliation step at
  load; persist `appliedSeedVersion`.
- `src/VibeModel/VibeModel.csproj` — two `<Copy>` items in `DeployAddin` for the seed + manifest.
- `src/VibeModel/Services/Claude/Commands/MemoryCommand.cs` (plan 08) — (phase 3) global-only
  export copy.
- `SettingsManager.cs` — (option) `appliedSeedVersion` getter/setter, if not a standalone marker.
- `SettingsDialog.cs` — (phase 3) "Export shareable memory…" button.
- `CLAUDE.md` / README — document shareable vs. never-shared files and the seed pipeline.
- `.gitignore` — ignore the maintainer's local `contributions/` folder.

## 8. Risks & test posture

- **Merge correctness is the whole ballgame.** A bug that duplicates or resurrects facts is the
  headline risk. Mitigated by making merge **pure and unit-tested** (JSONL-in → JSONL-out over id
  sets, no Revit needed) and by the idempotency invariant: running the same merge twice must
  produce an identical file. A property test ("apply seed N times == apply once") nails this.
- **Depends on plan 08's schema.** If plan 08 changes the entry shape or `MemoryStore` API, this
  plan's field references shift. Low blast radius — it's one reconciliation method plus a script.
- **Privacy is a human gate.** The structural guards (§5) reduce but don't eliminate reliance on
  maintainer review. Accepted: the repo commit is auditable and revertible, which is the right
  trust model for a locally-run, self-distributed tool.
- **Deploy copies are additive.** They inherit existing `Deploy` / `SkipDeploy` gating, so CI and
  worktree builds are unaffected; `Condition="Exists"` keeps builds green pre-seed.
- **Fits the workflow.** Phase 1 is a self-contained build → review → verify unit (verify: fresh
  profile inherits seed; delete a fact, bump `seedVersion`, confirm it stays gone). Phases 2–3
  layer on without reworking phase 1.
