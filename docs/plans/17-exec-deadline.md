# Plan 17 — runaway `exec` protection (loop deadline)

> Goal: an endless loop in an `exec` snippet no longer freezes Revit for good. Before plan 17 the
> main thread could not be aborted; the 30 s HTTP timeout only abandoned the request and Revit
> stayed busy until it was killed. pyRevit 7.0's agent runtime checks a deadline between Python
> lines; a compiled C# snippet has no "between lines", so VibeModel checks at the start of every
> loop body.

> **STATUS (built 2026-10-19, branch claude-bot/t-352).** Headless tests including a real CodeDom
> compile of an instrumented endless loop that stops after 1 s (`ExecDeadlineTests`); live in
> Revit 2023 — see "Verification" below.

## Decisions

- **Instrumentation, not a thread abort.** `ExecDeadline.Instrument` scans the snippet and puts
  ` __vm.Tick();` right after the `{` that opens every `for` / `foreach` / `while` / `do` body (on
  the same line, so the compiler's line numbers do not move). The wrapper's `Run` gets the
  `ExecClock` as a second parameter named `__vm`. `Thread.Abort` on Revit's main thread was never
  an option.
- **The scanner** classifies every character as code, comment, or literal: `//` and `/* */`
  comments, regular strings with escapes, verbatim `@"…"` with `""`, interpolated `$"…"` /
  `$@"…"` whose holes are scanned as code (a string inside a hole included), char literals.
  Keywords are matched on identifier boundaries (`format`, `undo()`, `@for`, `whileX` are not
  loops). `for/foreach/while (…)` → the matching `)` over code characters only → the next code
  character (comments skipped): `{` = instrumented, `;` = an empty loop or a do-while tail (no
  body), anything else = a brace-less body (counted, not instrumented). A snippet without a
  braced loop is returned as the same string.
- **Budget**: 25 s by default (under the 30 s an HTTP command waits); `?timeout=<s>` (1–300) for a
  deliberately long run. The HTTP wait for a request that runs exec is the budget + 10 s (compile
  and answer), never shorter than the usual wait (`ExecDeadline.Wait`); the socket send timeout
  follows `?timeout=`.
- **Firing**: `ExecClock.Tick` reads the performance counter on every call (tens of ns) — a
  loop whose body takes seconds is still stopped on time — and throws `ExecDeadlineException`;
  once fired every later tick throws again — but only while its exec runs: exec stops the clock when
  the snippet returns, so a loop it left behind (an Idling handler) never throws inside Revit's own
  events later. The wrapper re-throws after the snippet when the
  clock fired (a snippet that caught the exception is still not committed), so its transaction is
  rolled back. exec answers `ERROR: exec stopped after 25 s at a loop check (nothing kept).` +
  `Hint: narrow the collector or raise ?timeout=<s> (max 300).` (JSON code `EXEC_TIMEOUT`). When
  the snippet had committed transactions of its own before that (recorder count grew), the text
  says those were kept.
- **Honest limits**: a brace-less loop body, recursion, `goto` and a single long Revit API call
  are not interrupted. When such an exec outlives the HTTP wait, the timeout text adds
  `Hint: exec checks its time only at the start of a loop body written with { } …`. The prompt
  file tells the model to keep exec loops braced.

## Verification (Revit 2023, 2026-10-19 — same session as plan 15)

- `exec int n = 0; while (true) { n++; }` → `ERROR: exec stopped after 25 s at a loop check (nothing
  kept).` after 25.1 s; the next command (`info`) answered at once.
- With `timeout=40&format=json` → `{"ok":false,"error":{"code":"EXEC_TIMEOUT","message":"exec stopped
  after 40 s at a loop check (nothing kept).",…}}` after 40.1 s.
- A snippet that moves a wall and then swallows everything (`try { while (true) { } } catch
  (Exception) { }`) with `timeout=5` → stopped at 5.1 s, nothing kept.
- Overhead: a `foreach` over every element of the model summing ids, three runs each: 9–10 ms
  instrumented, 9–11 ms brace-less (not instrumented) — not measurable.
- **Changed in the code review, re-checked live afterwards (2026-10-19, 18:08)**: an exec with
  `timeout=1` subscribed an Idling handler holding a braced loop; 4 s and 7 s later the handler was
  still running (it rewrote its file both times, no exception) — the clock stops with its exec. The
  handler was removed afterwards.
