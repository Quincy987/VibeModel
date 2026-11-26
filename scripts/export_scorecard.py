"""Turn a VibeModel chat export into the same scorecard every time.

The input is what Diagnose -> "Exporteer chats..." (or `chatlog --export`) writes: one markdown
file (up to 5 chats) or a zip / unpacked folder with chats/*.md, INDEX.md and README.md. Raw chat
logs (%LOCALAPPDATA%/VibeModel/chats/*.json) work too; they carry the same steps.

Usage (stdlib only, run isolated):
    python -I scripts/export_scorecard.py <export.zip | folder | chats-*.md | chat.json ...> [--out <base>]

Writes <base>.md and <base>.json (default: next to the input, "scorecard-v<countingVersion>").
The numbers are stamped with COUNTING_VERSION: when a rule below changes, raise it, so two
scorecards are only compared when they were counted the same way.

Three numbers are HEURISTICS and are labelled as such in the output:
- hidden failures: an exec step marked ok that is directly followed by an exec step sending the
  same snippet again - rewritten >= 85 % the same, or its snippet file patched in place to remove
  a construct exec's C# 5 compiler rejects (?., `is T x`, $"", long ids, GetSlabShapeEditor) -
  so the first one most likely failed (a compile error the pane did not see). A dry run
  (readonly=1 / preview=1) followed by the real run is not counted.
- correction turns: a follow-up question that matches CORRECTION_RE (Dutch and English phrases
  such as "klopt niet", "je bent ... vergeten", "niet begrepen"). No hand audit.
- offer endings: the answer's last paragraph offers a next step ("Zal ik", "Wil je dat ik",
  "Shall I") or ends on a question mark.
"""

from __future__ import annotations

import argparse
import difflib
import io
import json
import os
import re
import statistics
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import successcase as sc  # noqa: E402  (turn pairing of raw chat logs)

COUNTING_VERSION = 1

# ---------------------------------------------------------------- the export's own words (nl + en)

CHAT_HEAD_RE = re.compile(r"^# .+\n\n- (?:Datum|Date): ", re.M)
TURN_RE = re.compile(r"^## (?:Beurt|Turn) (\d+) — ", re.M)
QUESTION_LABELS = ("**Vraag**", "**Question**")
ANSWER_RE = re.compile(r"^\*\*(?:Antwoord|Answer)\*\*(?: \((?P<dur>[\d.]+) s\))?\s*$", re.M)
COMMANDS_RE = re.compile(r"^\*\*Commands\*\*(?: \((?P<n>\d+)(?:, (?P<f>\d+) (?:mislukt|failed))?\))?(?P<rest>.*)$", re.M)
FULL_ARGS_RE = re.compile(r"^(?:Volledige argumenten van stap|Full arguments of step) (\d+) \((.*?)\):\s*$")
NOT_RECORDED_RE = re.compile(r"geen commands vastgelegd|no commands recorded", re.I)
FROM_LOG_RE = re.compile(r"uit de debuglog|from the debug log", re.I)
USAGE_RE = re.compile(r"^_(?P<tok>[\d.]+[kM]?) tokens(?P<rest>.*)_\s*$", re.M)
CONTEXT_RE = re.compile(r"(?:gesprekslengte|conversation length) ([\d.]+[kM]?) tokens")
# Since VibeModel 1.2.22 the export carries the build, change mode and standards pack per turn:
# "_VibeModel 1.2.22 · modus: ask · pakket: ib-amsterdam_" ("geen" / "none" = no pack).
BUILD_RE = re.compile(r"^_VibeModel (?P<ver>\S+) · (?:modus|mode): (?P<mode>[^·]+?) · (?:pakket|pack): (?P<pack>.+?)_\s*$", re.M)

SERVER_CALL_RE = re.compile(r"(?:127\.0\.0\.1|localhost|\[::1\]):\d+/(\w+)")
SNIPPET_FILE_RE = re.compile(r"[\w.\-]+\.cs\b", re.I)
EXIT_ONLY_RE = re.compile(r"^Exit code \d+$")

CORRECTION_RE = re.compile(
    r"niet begrepen|klopt niet|kloppen niet|klopt nog niet|niet goed|verkeerd|\bfout\b|vergeten|"
    r"ontbre(?:ekt|ken)|\bmist\b|missen|niet gelukt|lijkt er niet|nog steeds|werkt niet|"
    r"\bte (?:groot|klein|hoog|laag|lang|kort)\b|je hebt maar|sluit(?:en)? niet aan|steekt door|"
    r"niet de bedoeling|had .{1,40} moeten|waarom (?:is|zijn|heb|gaat|staat|staan)|"
    r"\bnee\b|(?<![_\w])dubbel(?:e|ingen)?\b|"
    r"you forgot|not right|that's wrong|is wrong|doesn't match|still not|didn't work|why (?:is|did|does)",
    re.I)
OFFER_RE = re.compile(r"\b(?:zal ik|wil je dat ik|zullen we|moet ik|shall i|should i|do you want me|want me to)\b", re.I)


# ---------------------------------------------------------------- reading

def read_sources(paths: list[str]) -> list[tuple[str, str]]:
    """(name, text) per chat document; a zip/folder/md export or raw chat JSON."""
    out: list[tuple[str, str]] = []
    for p in paths:
        if os.path.isdir(p):
            chats = os.path.join(p, "chats")
            folder = chats if os.path.isdir(chats) else p
            for name in sorted(os.listdir(folder)):
                full = os.path.join(folder, name)
                if name.lower().endswith(".md") and name not in ("INDEX.md", "README.md"):
                    out.extend(split_md(name, read_text(full)))
                elif name.lower().endswith(".json"):
                    out.append((name, json_to_md(full)))
        elif p.lower().endswith(".zip"):
            with zipfile.ZipFile(p) as z:
                for info in sorted(z.infolist(), key=lambda i: i.filename):
                    n = info.filename
                    if n.startswith("chats/") and n.lower().endswith(".md"):
                        out.extend(split_md(n, z.read(info).decode("utf-8-sig")))
        elif p.lower().endswith(".json"):
            out.append((os.path.basename(p), json_to_md(p)))
        else:
            out.extend(split_md(os.path.basename(p), read_text(p)))
    return out


def read_text(path: str) -> str:
    with io.open(path, encoding="utf-8-sig") as f:
        return f.read()


def split_md(name: str, text: str) -> list[tuple[str, str]]:
    """A single-file export holds a README and several chats. Split at each chat's own heading
    (a '# title' line followed by its '- Datum:' / '- Date:' line), never at '---': an answer can
    contain a markdown rule of its own. The separator the writer puts between chats is dropped."""
    starts = [m.start() for m in CHAT_HEAD_RE.finditer(text)]
    if len(starts) <= 1:
        return [(name, text)]
    docs = [re.sub(r"\n---\s*$", "\n", text[a:b].rstrip()) for a, b in zip(starts, starts[1:] + [len(text)])]
    return [(f"{name}#{i + 1}", d) for i, d in enumerate(docs)]


def pair_like_export(messages: list[dict]) -> list[dict]:
    """Each question with the next message that is not a question - as ChatExport.Turns pairs them, so
    an error turn (role "system", which keeps its steps) counts the same from JSON as from markdown.
    successcase.pair_turns takes only assistant replies, which is right for case files, not here."""
    turns: list[dict] = []
    for m in messages:
        if (m.get("role") or "").lower() == "user":
            turns.append({"question": m, "answer": None})
        elif turns and turns[-1]["answer"] is None:
            turns[-1]["answer"] = m
    return turns


def json_to_md(path: str) -> str:
    """A raw chat log as the export would render it (only what the scorecard reads)."""
    chat = sc.load_chat(path)
    lines = [f"# {chat.get('title') or chat.get('id') or 'chat'}", ""]
    for i, t in enumerate(pair_like_export(chat["messages"]), 1):
        lines += [f"## Turn {i} — ?", "", "**Question**", "", (t["question"].get("content") or "").strip(), ""]
        a = t["answer"] or {}
        steps = a.get("steps")
        if steps is None:
            lines += ["**Commands**", "", "_no commands recorded_", ""]
        else:
            failed = sum(1 for s in steps if not s.get("ok", True))
            lines += [f"**Commands** ({len(steps)}" + (f", {failed} failed" if failed else "") + ")", ""]
            full = []
            if steps:
                lines += ["| # | command | args | result | time |", "|---|---|---|---|---|"]
            for n, s in enumerate(steps, 1):
                res = "✓" if s.get("ok", True) else "✗ " + (s.get("error") or "")
                args = s.get("args") or ""
                lines.append(f"| {n} | {s.get('command') or ''} | {one_line(args)[:200]} | {one_line(res)} | |")
                full.append((n, s.get("command") or "", args))
            lines.append("")
            for n, cmd, args in full:
                lines += [f"Full arguments of step {n} ({cmd}):", "", "```text", args, "```", ""]
        lines += ["**Answer**", "", (a.get("content") or "").strip(), ""]
        u = a.get("usage")
        if u:
            tok = sum(int(u.get(k) or 0) for k in ("inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens"))
            ctx = int(u.get("contextTokens") or 0)
            lines += [f"_{tok} tokens" + (f" · conversation length {ctx} tokens" if ctx else "") + "_", ""]
        if any(k in a for k in ("version", "mode", "pack")):
            lines += [f"_VibeModel {a.get('version') or '?'} · mode: {a.get('mode') or '?'} · pack: {a.get('pack') or 'none'}_", ""]
    return "\n".join(lines)


def one_line(s: str) -> str:
    return re.sub(r"\s+", " ", s or "").strip().replace("|", "\\|")


# ---------------------------------------------------------------- parsing one chat

def parse_tokens(s: str) -> int:
    s = s.strip()
    mult = 1
    if s.endswith("k"):
        mult, s = 1000, s[:-1]
    elif s.endswith("M"):
        mult, s = 1_000_000, s[:-1]
    try:
        return int(round(float(s) * mult))
    except ValueError:
        return 0


def table_rows(block: str) -> list[dict]:
    rows = []
    for line in block.splitlines():
        # The table ends where the full arguments begin: a code line there may look like a row.
        if FULL_ARGS_RE.match(line):
            break
        if not line.startswith("| ") or line.startswith("| # ") or line.startswith("|---"):
            continue
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip())[1:-1]]
        if len(cells) < 4 or not cells[0].isdigit():
            continue
        result = cells[3]
        ok = not result.startswith("✗")
        rows.append({"n": int(cells[0]), "command": cells[1], "args": cells[2].replace("\\|", "|"),
                     "ok": ok, "error": "" if ok else result[1:].strip()})
    return rows


def full_args(block: str) -> dict[int, str]:
    """Step number -> the full arguments from the fenced blocks below the table."""
    out: dict[int, str] = {}
    lines = block.splitlines()
    i = 0
    while i < len(lines):
        m = FULL_ARGS_RE.match(lines[i])
        if m:
            j = i + 1
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and (lines[j].startswith("```") or lines[j].startswith("~~~~")):
                fence = "~~~~" if lines[j].startswith("~~~~") else "```"
                k = j + 1
                body = []
                while k < len(lines) and not lines[k].startswith(fence):
                    body.append(lines[k])
                    k += 1
                out[int(m.group(1))] = "\n".join(body)
                i = k
        i += 1
    return out


def parse_chat(name: str, text: str) -> dict:
    title_m = re.search(r"^# (.*)$", text, re.M)
    chat = {"name": name, "title": title_m.group(1).strip() if title_m else name, "turns": []}
    starts = [m.start() for m in TURN_RE.finditer(text)] + [len(text)]
    for a, b in zip(starts, starts[1:]):
        block = text[a:b]
        chat["turns"].append(parse_turn(block))
    return chat


def parse_turn(block: str) -> dict:
    turn = {"question": "", "answer": "", "steps": None, "fromLog": False, "tokens": None, "context": None,
            "durationS": None, "build": None}
    ans = ANSWER_RE.search(block)
    head = block[: ans.start()] if ans else block
    tail = block[ans.end():] if ans else ""
    # Question: the text between the label and the commands heading.
    q_start = min((head.find(lbl) for lbl in QUESTION_LABELS if lbl in head), default=-1)
    cmd = COMMANDS_RE.search(head)
    if q_start >= 0:
        q_end = cmd.start() if cmd else len(head)
        q = head[q_start:q_end]
        turn["question"] = q.split("**", 2)[-1].strip()
    if cmd:
        cmd_block = head[cmd.start():]
        if NOT_RECORDED_RE.search(cmd_block) and cmd.group("n") is None:
            turn["steps"] = None
        elif FROM_LOG_RE.search(cmd.group("rest") or ""):
            turn["fromLog"] = True
        else:
            rows = table_rows(cmd_block)
            fulls = full_args(cmd_block)
            for r in rows:
                r["fullArgs"] = fulls.get(r["n"], r["args"])
            turn["steps"] = rows
    if ans:
        turn["durationS"] = float(ans.group("dur")) if ans.group("dur") else None
        usage = USAGE_RE.search(tail)
        body = tail[: usage.start()] if usage else tail
        build = BUILD_RE.search(tail)
        if build:
            turn["build"] = {k: build.group(k).strip() for k in ("ver", "mode", "pack")}
            body = body.replace(build.group(0), "")
        turn["answer"] = body.strip()
        if usage:
            turn["tokens"] = parse_tokens(usage.group("tok"))
            c = CONTEXT_RE.search(usage.group("rest"))
            if c:
                turn["context"] = parse_tokens(c.group(1))
    return turn


# ---------------------------------------------------------------- classifying

def failure_class(step: dict) -> str:
    err = step["error"]
    args = step.get("fullArgs") or step["args"]
    if re.search(r"Exit code 28\b", err):
        return "timeout"
    if re.search(r"Exit code 255\b", err) and re.search(r"Select-Object|Select-String", args):
        return "pipe"
    if re.search(r"Exit code (?:49|26)\b", err) or re.search(r"Permission to use|is blocked|not recognized|command not found", err, re.I):
        return "missing tool"
    # The CLI's own file tools ("File does not exist. Note: your current working directory…"); a
    # VibeModel "Element 999 not found" is a server answer, counted below.
    if re.search(r"^(?:File|Path) does not exist|No such file|cannot find (?:the )?(?:file|path)", err, re.I):
        return "file not found"
    if EXIT_ONLY_RE.match(err) and re.search(r"--data-urlencode\s+\"args=", args) and not re.search(r"args@", args):
        return "PowerShell inline"
    # Since VibeModel 1.2.22 exec fails its own compile and run-time errors (they read as ok before).
    if re.search(r"did not compile|COMPILATION ERRORS", err):
        return "exec compile"
    if re.search(r"^exec failed:", err):
        return "exec run time"
    if re.search(r"Revit rolled|^in exec:|ERROR|snippet changed|Pick one of|Missing or invalid token|Discarded|refused|not found|Unknown command", err, re.I):
        return "Revit/VibeModel error"
    if EXIT_ONLY_RE.match(err):
        return "other exit code"
    return "other"


def is_exec(step: dict) -> bool:
    return step["command"].lower() == "exec"


def is_dry_run(args: str) -> bool:
    return bool(re.search(r"(?:readonly|preview)=1", args))


# Constructs exec's compiler (C# 5, Revit 2023 API) rejects. A patch that replaces one of them is a
# compile fix, so the run before it failed.
COMPILE_FIX_RE = re.compile(r"\?\.|\bis\s+[A-Z]\w*\s+[a-z_]\w*|\$\"|\blong\b|GetSlabShapeEditor|\bout\s+var\b|\bnameof\(")
PATCH_OLD_RE = re.compile(r"\.Replace\(\s*(['\"])(.*?)\1\s*,|-replace\s+'(.*?)'", re.S)


def is_resend(a: str, b: str) -> bool:
    """b sends a's snippet again: rewritten nearly the same, or a's snippet file patched in place to
    remove a construct the compiler rejects."""
    if len(a) >= 40 and len(b) >= 40 and \
            difflib.SequenceMatcher(None, a[:4000], b[:4000], autojunk=False).ratio() >= 0.85:
        return True
    fa, fb = set(SNIPPET_FILE_RE.findall(a)), set(SNIPPET_FILE_RE.findall(b))
    if not (fa & fb):
        return False
    for m in PATCH_OLD_RE.finditer(b):
        old = m.group(2) if m.group(2) is not None else m.group(3).replace("\\", "")
        if COMPILE_FIX_RE.search(old or ""):
            return True
    return False


def hidden_failures(steps: list[dict]) -> int:
    n = 0
    for s, nxt in zip(steps, steps[1:]):
        if not (s["ok"] and is_exec(s) and is_exec(nxt)):
            continue
        a, b = s.get("fullArgs") or s["args"], nxt.get("fullArgs") or nxt["args"]
        if is_dry_run(a) and not is_dry_run(b):
            continue
        if is_resend(a, b):
            n += 1
    return n


def ends_on_offer(answer: str) -> tuple[bool, bool]:
    """(offers a next step, ends on a question mark) for the answer's last paragraph."""
    paras = [p.strip() for p in re.split(r"\n\s*\n", answer or "") if p.strip()]
    if not paras:
        return False, False
    last = paras[-1]
    return bool(OFFER_RE.search(last)), last.rstrip().endswith("?")


# ---------------------------------------------------------------- the scorecard

def score(chats: list[dict]) -> dict:
    turns = [t for c in chats for t in c["turns"]]
    recorded = [t for t in turns if t["steps"] is not None]
    steps = [s for t in recorded for s in t["steps"]]
    failed = [s for s in steps if not s["ok"]]
    classes: dict[str, int] = {}
    for s in failed:
        k = failure_class(s)
        classes[k] = classes.get(k, 0) + 1
    hidden = sum(hidden_failures(t["steps"]) for t in recorded)
    multi = sum(1 for s in steps if len(SERVER_CALL_RE.findall(s.get("fullArgs") or s["args"])) > 1)
    followups = [t for c in chats for t in c["turns"][1:]]
    corrections = [t for t in followups if CORRECTION_RE.search(t["question"] or "")]
    offers = [ends_on_offer(t["answer"]) for t in turns if t["answer"]]
    tokens = [t["tokens"] for t in turns if t["tokens"]]
    contexts = [t["context"] for t in turns if t["context"]]
    builds = sorted({(t["build"]["ver"], t["build"]["mode"], t["build"]["pack"]) for t in turns if t["build"]})

    def share(a: int, b: int) -> float | None:
        return round(a / b, 3) if b else None

    return {
        "countingVersion": COUNTING_VERSION,
        "chats": len(chats),
        "chatsWithRecordedCommands": sum(1 for c in chats if any(t["steps"] is not None for t in c["turns"])),
        "turns": len(turns),
        "turnsWithRecordedCommands": len(recorded),
        "commands": len(steps),
        "failed": len(failed),
        "failedShare": share(len(failed), len(steps)),
        "failedExitCodeOnly": sum(1 for s in failed if EXIT_ONLY_RE.match(s["error"])),
        "failureClasses": dict(sorted(classes.items(), key=lambda kv: -kv[1])),
        "hiddenFailuresHeuristic": hidden,
        "failedIncludingHiddenShare": share(len(failed) + hidden, len(steps)),
        "stepsWithSeveralServerCalls": multi,
        "followUpTurns": len(followups),
        "correctionTurnsHeuristic": len(corrections),
        "correctionsPer10FollowUps": round(10 * len(corrections) / len(followups), 1) if followups else None,
        "answers": len(offers),
        "answersOfferingNextStep": sum(1 for o, _ in offers if o),
        "answersEndingOnQuestion": sum(1 for _, q in offers if q),
        "medianTokensPerTurn": int(statistics.median(tokens)) if tokens else None,
        "peakContext": max(contexts) if contexts else None,
        "builds": [{"version": v, "mode": m, "pack": p} for v, m, p in builds],
    }


def render_md(card: dict, sources: list[str]) -> str:
    def pct(x):
        return "n.b." if x is None else f"{100 * x:.1f} %"

    def num(x):
        return "n.b." if x is None else f"{x:,}".replace(",", ".")

    lines = [
        f"# Scorecard (counting version {card['countingVersion']})",
        "",
        "Bron: " + ", ".join(os.path.basename(s) for s in sources),
        "",
        "```",
        f"chats                               {card['chats']} ({card['chatsWithRecordedCommands']} met vastgelegde commands)",
        f"beurten                             {card['turns']} ({card['turnsWithRecordedCommands']} met vastgelegde commands)",
        f"commands                            {card['commands']}",
        f"mislukt                             {card['failed']} ({pct(card['failedShare'])})",
        f"  waarvan alleen 'Exit code N'      {card['failedExitCodeOnly']}",
        f"verborgen mislukt (heuristiek)      {card['hiddenFailuresHeuristic']}",
        f"mislukt incl. verborgen             {pct(card['failedIncludingHiddenShare'])}",
        f"stappen met meer serveraanroepen    {card['stepsWithSeveralServerCalls']}",
        f"correctiebeurten (heuristiek)       {card['correctionTurnsHeuristic']} van {card['followUpTurns']} vervolgvragen"
        f" ({'n.b.' if card['correctionsPer10FollowUps'] is None else card['correctionsPer10FollowUps']} per 10)",
        f"antwoorden met aanbod volgende stap {card['answersOfferingNextStep']} van {card['answers']}",
        f"antwoorden eindigend op vraag       {card['answersEndingOnQuestion']} van {card['answers']}",
        f"mediaan tokens per beurt            {num(card['medianTokensPerTurn'])}",
        f"hoogste gesprekslengte              {num(card['peakContext'])}",
        "```",
        "",
        "## Mislukt per oorzaak",
        "",
        "```",
    ]
    for k, v in card["failureClasses"].items():
        lines.append(f"{k:<28}{v}")
    lines += ["```", ""]
    if card["builds"]:
        lines += ["## Versie, modus, pakket", ""]
        lines += [f"- VibeModel {b['version']} · {b['mode']} · {b['pack']}" for b in card["builds"]]
        lines.append("")
    lines += [
        "Heuristieken (zie de kop van scripts/export_scorecard.py): verborgen mislukt = een exec die als",
        "gelukt staat en direct daarna hetzelfde script opnieuw verstuurt (bijna gelijk herschreven, of het",
        "scriptbestand gepatcht op iets wat de C# 5-compiler weigert); correctiebeurt = een vervolgvraag",
        "met een correctiezin; aanbod = de laatste alinea biedt een volgende stap aan.",
        "",
    ]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("sources", nargs="+", help="export zip, unpacked export folder, chats-*.md, or chat JSON files")
    ap.add_argument("--out", help="output base path without extension (default: next to the first source)")
    a = ap.parse_args(argv)
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    docs = read_sources(a.sources)
    if not docs:
        print("Geen chats gevonden in " + ", ".join(a.sources), file=sys.stderr)
        return 1
    chats = [parse_chat(n, t) for n, t in docs]
    card = score(chats)
    base = a.out
    if not base:
        first = os.path.abspath(a.sources[0])
        folder = first if os.path.isdir(first) else os.path.dirname(first)
        base = os.path.join(folder, f"scorecard-v{COUNTING_VERSION}")
    with io.open(base + ".json", "w", encoding="utf-8") as f:
        json.dump(card, f, indent=2, ensure_ascii=False)
    md = render_md(card, a.sources)
    with io.open(base + ".md", "w", encoding="utf-8") as f:
        f.write(md)
    sys.stdout.write(md)
    return 0


if __name__ == "__main__":
    sys.exit(main())
