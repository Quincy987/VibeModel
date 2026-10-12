"""Turn VibeModel chat logs into successcase files for the two-week werkvoorbereiders trial.

One case file per chat (<out>/<date>-<slug>.md) with a fixed template, plus INDEX.md
(regenerated every run). Rerunning is safe: the hand-filled fields - Wie, Kwaliteit and
Opmerkingen - survive, only the generated parts are refreshed.

Usage (stdlib only, run isolated):
    python -I scripts/successcase.py 20261012-101908-725070b0 20261012-114104-4510601a
    python -I scripts/successcase.py "%LOCALAPPDATA%/VibeModel/chats/20261012-*.json" --out demo_out/successcases
    python -I scripts/successcase.py --index-only

A chat argument is a path, a glob, or a bare chat id (looked up in %LOCALAPPDATA%/VibeModel/chats).
Commands per turn are not in the chat log; they are counted from the VibeModel debug log
(%LOCALAPPDATA%/VibeModel/logs/log_<date>.txt, local time) between question and answer.
Indicative only: every client on this machine that talks to Revit logs to the same file.
"""

from __future__ import annotations

import argparse
import datetime as dt
import glob
import json
import os
import re
import sys
import unicodedata

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO_ROOT, "demo_out", "successcases")
VIBEMODEL_DATA = os.path.join(os.environ.get("LOCALAPPDATA", ""), "VibeModel")
DEFAULT_CHATS = os.path.join(VIBEMODEL_DATA, "chats")
DEFAULT_LOGS = os.path.join(VIBEMODEL_DATA, "logs")

ANSWER_LINES = 10
QUALITY_SCALE = (
    "Schaal: 5 = correct en direct bruikbaar · 4 · 3 = bruikbaar na correctie · 2 · "
    "1 = fout/misleidend"
)
META_RE = re.compile(r"<!-- successcase (\{.*?\}) -->")
SECTION_RE = re.compile(r"^## (.+?)\s*$", re.M)
WIE_RE = re.compile(r"^- \*\*Wie:\*\*[ \t]*(.*)$", re.M)
SCORE_RE = re.compile(r"^Score \(1-5\):[ \t]*(.*)$", re.M)
CASENR_RE = re.compile(r"^- \*\*Case nr:\*\*[ \t]*(\d+)", re.M)
OPMERKINGEN_PLACEHOLDER = "_(invullen)_"

# Leading one-line progress notes ("Let me look...") the CLI backend streams before the answer.
NARRATION_RE = re.compile(
    r"^(let me|i'll|i will|i'm|now |first|next|ok|okay|laat me|ik ga|ik kijk|eerst|nu )",
    re.I,
)


# ---------------------------------------------------------------- time helpers

def parse_utc(stamp: str) -> dt.datetime:
    """'2026-10-12T10:19:08.2668169Z' -> aware UTC datetime (.NET writes 7 fraction digits)."""
    m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?", stamp or "")
    if not m:
        raise ValueError(f"bad timestamp: {stamp!r}")
    base = dt.datetime.strptime(m.group(1), "%Y-%m-%dT%H:%M:%S")
    micro = int((m.group(2) or "0")[:6].ljust(6, "0"))
    return base.replace(microsecond=micro, tzinfo=dt.timezone.utc)


def local(t: dt.datetime) -> dt.datetime:
    return t.astimezone()


def fmt_duration(seconds: float | None) -> str:
    if seconds is None:
        return "n.b."
    s = int(round(seconds))
    if s < 60:
        return f"{s}s"
    m, s = divmod(s, 60)
    if m < 60:
        return f"{m}m{s:02d}s"
    h, m = divmod(m, 60)
    return f"{h}u{m:02d}m"


# ---------------------------------------------------------------- chat log

def resolve_chat_args(args: list[str], chats_dir: str = DEFAULT_CHATS) -> list[str]:
    paths: list[str] = []
    for a in args:
        if os.path.isfile(a):
            paths.append(a)
            continue
        hits = sorted(glob.glob(os.path.expandvars(a)))
        if not hits:
            candidate = os.path.join(chats_dir, a if a.endswith(".json") else a + ".json")
            hits = [candidate] if os.path.isfile(candidate) else []
        if not hits:
            raise SystemExit(f"Chatlog niet gevonden: {a}")
        paths.extend(hits)
    seen: set[str] = set()
    return [p for p in (os.path.abspath(p) for p in paths) if not (p in seen or seen.add(p))]


def load_chat(path: str) -> dict:
    with open(path, encoding="utf-8-sig") as f:
        data = json.load(f)
    if not isinstance(data, dict) or not isinstance(data.get("messages"), list):
        raise ValueError(f"{path}: geen VibeModel-chatlog (messages ontbreekt)")
    data["_path"] = os.path.abspath(path)
    return data


def pair_turns(messages: list[dict]) -> list[dict]:
    """Each user message with the assistant reply that follows it (None if it never came)."""
    turns: list[dict] = []
    for m in messages:
        role = (m.get("role") or "").lower()
        if role == "user":
            turns.append({"question": m, "answer": None})
        elif role == "assistant" and turns and turns[-1]["answer"] is None:
            turns[-1]["answer"] = m
    return turns


def condense_answer(text: str, max_lines: int = ANSWER_LINES) -> str:
    """First lines of the final answer, skipping the one-line progress notes in front of it."""
    paras = [p.strip() for p in re.split(r"\n\s*\n", (text or "").strip()) if p.strip()]
    while len(paras) > 1 and "\n" not in paras[0] and NARRATION_RE.match(paras[0]):
        paras.pop(0)
    lines = [ln.rstrip() for ln in "\n".join(paras).splitlines() if ln.strip()]
    if not lines:
        return "_(geen antwoord)_"
    out = "\n".join(lines[:max_lines])
    if len(lines) > max_lines:
        out += f"\n\n_… ingekort ({len(lines) - max_lines} regels meer, zie chatlog)_"
    return out


# ---------------------------------------------------------------- debug log (commands)

LOG_LINE_RE = re.compile(r"^\[(\d\d):(\d\d):(\d\d)\.(\d+)\] \[\w+\] Executing(?: batch: (\d+) commands|:)")


def load_command_events(logs_dir: str, day: dt.date, cache: dict) -> list[tuple[dt.datetime, int]] | None:
    """(local time, command count) for every executed command/batch on a local day; None = no log."""
    if day in cache:
        return cache[day]
    path = os.path.join(logs_dir, f"log_{day.isoformat()}.txt")
    events = None
    if os.path.isfile(path):
        events = []
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = LOG_LINE_RE.match(line)
                if not m:
                    continue
                h, mi, s, frac, n = m.groups()
                # Log times are naive local wall-clock; astimezone() applies that day's offset.
                t = dt.datetime(day.year, day.month, day.day, int(h), int(mi), int(s),
                                int(frac[:6].ljust(6, "0"))).astimezone()
                events.append((t, int(n) if n else 1))
    cache[day] = events
    return events


def count_commands(start: dt.datetime, end: dt.datetime, logs_dir: str, cache: dict) -> int | None:
    a, b = local(start), local(end)
    total, found = 0, False
    day = a.date()
    while day <= b.date():
        events = load_command_events(logs_dir, day, cache)
        if events is not None:
            found = True
            total += sum(n for t, n in events if a <= t <= b)
        day += dt.timedelta(days=1)
    return total if found else None


# ---------------------------------------------------------------- case model

def slugify(text: str, max_len: int = 40) -> str:
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode()
    text = re.sub(r"[^a-zA-Z0-9]+", "-", text.lower()).strip("-")
    return (text[:max_len].rstrip("-")) or "case"


def build_chat_case(chat: dict, logs_dir: str = DEFAULT_LOGS) -> dict:
    """Everything a chat log can tell us, as a case dict (no hand-filled fields)."""
    turns = pair_turns(chat["messages"])
    if not turns:
        raise ValueError(f"{chat['_path']}: geen gebruikersvraag in de chat")
    cache: dict = {}
    rows = []
    work = 0.0
    for i, t in enumerate(turns, 1):
        q_at = parse_utc(t["question"]["timestampUtc"])
        a_at = parse_utc(t["answer"]["timestampUtc"]) if t["answer"] else None
        secs = (a_at - q_at).total_seconds() if a_at else None
        if secs is not None:
            work += secs
        cmds = count_commands(q_at, a_at, logs_dir, cache) if a_at else None
        rows.append({"nr": i, "at": local(q_at), "secs": secs, "commands": cmds,
                     "question": t["question"].get("content", "")})
    first_q = parse_utc(turns[0]["question"]["timestampUtc"])
    answers = [t["answer"] for t in turns if t["answer"]]
    last_a = parse_utc(answers[-1]["timestampUtc"]) if answers else None
    return {
        "chat": chat.get("id") or os.path.splitext(os.path.basename(chat["_path"]))[0],
        "chat_path": chat["_path"],
        "date": local(first_q).date().isoformat(),
        "model": chat.get("project") or "",
        "question": turns[0]["question"].get("content", "").strip(),
        "answer": condense_answer(answers[-1].get("content", "")) if answers else "_(geen antwoord)_",
        "turns": rows,
        "lead_secs": (last_a - first_q).total_seconds() if last_a else None,
        "work_secs": work if answers else None,
        "slug_source": chat.get("title") or turns[0]["question"].get("content", ""),
    }


# ---------------------------------------------------------------- render / parse / merge

def render_case(case: dict) -> str:
    meta = {k: case[k] for k in ("chat", "mail") if case.get(k)}
    nr = case.get("nr", "")
    title = re.sub(r"\s+", " ", case.get("question", "")).strip()
    title = title[:70] + ("…" if len(title) > 70 else "")
    out = [
        f"# Case {nr} — {title}",
        "",
        f"<!-- successcase {json.dumps(meta, ensure_ascii=False, sort_keys=True)} -->",
        "<!-- Handmatig in te vullen: Wie, Score en Opmerkingen. De rest wordt bij elke run ververst. -->",
        "",
        f"- **Case nr:** {nr}",
        f"- **Datum:** {case.get('date', '')}",
        f"- **Wie:** {case.get('wie', '')}".rstrip(),
        f"- **Model:** {case.get('model') or 'n.b.'}",
        "",
        "## Vraag",
        "",
        quote(case.get("question") or "_(nog geen vraag)_"),
        "",
        "## Antwoord",
        "",
        case.get("answer") or "_(nog niet gekoppeld aan een chat)_",
        "",
        "## Verloop",
        "",
    ]
    turns = case.get("turns") or []
    if turns:
        out += ["| # | Vraag om | Antwoord na | Commands | Vraag |", "|---|---|---|---|---|"]
        for r in turns:
            q = re.sub(r"\s+", " ", r["question"]).strip().replace("|", "/")
            q = q[:60] + ("…" if len(q) > 60 else "")
            cmds = "n.b." if r["commands"] is None else str(r["commands"])
            out.append(f"| {r['nr']} | {r['at']:%H:%M:%S} | {fmt_duration(r['secs'])} | {cmds} | {q} |")
        out += ["", "_Commands geteld uit de VibeModel-debuglog tussen vraag en antwoord (indicatief)._"]
    else:
        out.append("_(nog niet gekoppeld: `successcase_mail.py --case <dit bestand> --chat <chatlog>`)_")
    out += [
        "",
        "## Totale tijd tot oplossing",
        "",
    ]
    if case.get("lead_secs") is not None:
        out.append(f"{fmt_duration(case['lead_secs'])} doorlooptijd (eerste vraag → laatste antwoord), "
                   f"waarvan {fmt_duration(case.get('work_secs'))} VibeModel aan het werk.")
    else:
        out.append("n.b.")
    out += [
        "",
        "## Kwaliteit",
        "",
        f"Score (1-5): {case.get('score', '')}".rstrip(),
        "",
        QUALITY_SCALE,
        "",
        "## Opmerkingen",
        "",
        case.get("opmerkingen") or OPMERKINGEN_PLACEHOLDER,
        "",
        "## Chatlog",
        "",
        f"`{case['chat_path']}`" if case.get("chat_path") else "n.b.",
        "",
    ]
    return "\n".join(out)


def quote(text: str) -> str:
    return "\n".join("> " + ln if ln.strip() else ">" for ln in text.strip().splitlines())


def unquote(text: str) -> str:
    return "\n".join(re.sub(r"^> ?", "", ln) for ln in text.strip().splitlines())


def sections(text: str) -> dict[str, str]:
    parts = SECTION_RE.split(text)
    return {parts[i].strip(): parts[i + 1].strip() for i in range(1, len(parts) - 1, 2)}


def parse_case(text: str) -> dict:
    """The fields a rerun must not lose, read back from an existing case file."""
    m = META_RE.search(text)
    meta = json.loads(m.group(1)) if m else {}
    secs = sections(text)
    wie = WIE_RE.search(text)
    nr = CASENR_RE.search(text)
    score = SCORE_RE.search(secs.get("Kwaliteit", ""))
    opm = secs.get("Opmerkingen", "")
    info = {
        "chat": meta.get("chat"),
        "mail": meta.get("mail"),
        "nr": int(nr.group(1)) if nr else None,
        "wie": wie.group(1).strip() if wie else "",
        "score": score.group(1).strip() if score else "",
        "opmerkingen": "" if opm == OPMERKINGEN_PLACEHOLDER else opm,
        "question": unquote(secs.get("Vraag", "")),
    }
    date = re.search(r"^- \*\*Datum:\*\*[ \t]*(.*)$", text, re.M)
    model = re.search(r"^- \*\*Model:\*\*[ \t]*(.*)$", text, re.M)
    info["date"] = date.group(1).strip() if date else ""
    info["model"] = "" if not model or model.group(1).strip() == "n.b." else model.group(1).strip()
    return info


def merge(generated: dict, existing: dict | None) -> dict:
    """Generated parts win, except hand-filled fields and a mail's own question/sender/date."""
    case = dict(generated)
    if not existing:
        return case
    case["nr"] = existing.get("nr") or case.get("nr")
    for field in ("wie", "score", "opmerkingen"):
        if existing.get(field):
            case[field] = existing[field]
    if existing.get("mail") and not case.get("mail"):
        # A chat rerun on a mail case: the mail is the real question; the chat only supplies the answer.
        case["mail"] = existing["mail"]
        case["question"] = existing.get("question") or case.get("question")
        case["date"] = existing.get("date") or case.get("date")
    if existing.get("chat") and not case.get("chat"):
        case["chat"] = existing["chat"]
    return case


# ---------------------------------------------------------------- store

def list_cases(out_dir: str) -> list[tuple[str, dict]]:
    found = []
    for path in sorted(glob.glob(os.path.join(out_dir, "*.md"))):
        if os.path.basename(path) == "INDEX.md":
            continue
        with open(path, encoding="utf-8") as f:
            text = f.read()
        if META_RE.search(text):
            found.append((path, parse_case(text)))
    return found


def find_case(out_dir: str, chat: str | None = None, mail_key: str | None = None) -> tuple[str, dict] | None:
    for path, info in list_cases(out_dir):
        if chat and info.get("chat") == chat:
            return path, info
        if mail_key and (info.get("mail") or {}).get("key") == mail_key:
            return path, info
    return None


def next_nr(out_dir: str) -> int:
    return max([info.get("nr") or 0 for _, info in list_cases(out_dir)] + [0]) + 1


def write_case(out_dir: str, generated: dict, existing_path: str | None = None) -> str:
    """Merge with the existing file (found by chat id / mail key, or given) and write it."""
    os.makedirs(out_dir, exist_ok=True)
    hit = None
    if existing_path:
        with open(existing_path, encoding="utf-8") as f:
            hit = (existing_path, parse_case(f.read()))
    else:
        hit = find_case(out_dir, generated.get("chat"), (generated.get("mail") or {}).get("key"))
    case = merge(generated, hit[1] if hit else None)
    if not case.get("nr"):
        case["nr"] = next_nr(out_dir)
    path = hit[0] if hit else os.path.join(
        out_dir, f"{case.get('date') or 'undated'}-{slugify(generated.get('slug_source') or case.get('question', ''))}.md")
    if not hit and os.path.exists(path):
        path = path[:-3] + f"-{case['nr']}.md"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(render_case(case))
    return path


def write_index(out_dir: str) -> str:
    rows = []
    for path, info in list_cases(out_dir):
        with open(path, encoding="utf-8") as f:
            secs = sections(f.read())
        total = secs.get("Totale tijd tot oplossing", "n.b.")
        m = re.match(r"(\S+) doorlooptijd .*?waarvan (\S+) VibeModel", total)
        total = f"{m.group(1)} (werk {m.group(2)})" if m else total.splitlines()[0] if total else "n.b."
        q = re.sub(r"\s+", " ", info.get("question", "")).strip().replace("|", "/")
        q = q[:80] + ("…" if len(q) > 80 else "")
        rows.append((info.get("nr") or 0, info.get("date", ""), info.get("model") or "n.b.", q, total,
                     info.get("score") or "–", os.path.basename(path)))
    rows.sort(key=lambda r: (r[1], r[0]))
    scored = [int(r[5]) for r in rows if r[5].isdigit()]
    lines = [
        "# Successcases — index",
        "",
        f"_Gegenereerd {dt.datetime.now():%Y-%m-%d %H:%M} door `scripts/successcase.py`; niet met de hand bewerken._",
        "",
        f"{len(rows)} cases, {len(scored)} beoordeeld"
        + (f", gemiddelde kwaliteit {sum(scored) / len(scored):.1f}" if scored else "") + ".",
        "",
        "| Nr | Datum | Model | Vraag | Totale tijd | Kwaliteit |",
        "|---|---|---|---|---|---|",
    ]
    for nr, date, model, q, total, score, name in rows:
        lines.append(f"| [{nr}]({name}) | {date} | {model} | {q} | {total} | {score} |")
    path = os.path.join(out_dir, "INDEX.md")
    os.makedirs(out_dir, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return path


# ---------------------------------------------------------------- CLI

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Chatlogs -> successcase-bestanden + INDEX.md")
    p.add_argument("chats", nargs="*", help="chatlog-pad, glob of chat-id")
    p.add_argument("--out", default=DEFAULT_OUT, help=f"uitvoermap (standaard {DEFAULT_OUT})")
    p.add_argument("--logs", default=DEFAULT_LOGS, help="VibeModel-logmap voor het tellen van commands")
    p.add_argument("--index-only", action="store_true", help="alleen INDEX.md opnieuw opbouwen")
    a = p.parse_args(argv)
    if not a.chats and not a.index_only:
        p.error("geef minstens één chatlog (of --index-only)")
    for path in resolve_chat_args(a.chats):
        try:
            case = build_chat_case(load_chat(path), a.logs)
        except (ValueError, json.JSONDecodeError) as e:
            print(f"OVERGESLAGEN {path}: {e}", file=sys.stderr)
            continue
        print(f"case: {write_case(a.out, case)}")
    print(f"index: {write_index(a.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
