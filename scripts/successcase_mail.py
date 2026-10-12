"""Create successcase files from colleagues' Outlook mails, and link them to a chat later.

    python -I scripts/successcase_mail.py "hoeksteen"                    # newest mail whose subject matches
    python -I scripts/successcase_mail.py --since 2026-10-12 --query revit   # every matching mail since a date
    python -I scripts/successcase_mail.py --since 2026-10-12 --dry-run       # only list what would be created
    python -I scripts/successcase_mail.py "hoeksteen" --chat 20261012-101908-725070b0   # mail + chat in one go
    python -I scripts/successcase_mail.py --case demo_out/successcases/<file>.md --chat <chatlog>   # link offline

Vraag = the mail body, Wie = the sender. Verloop/Antwoord stay empty until a chat is linked with
--chat (then the chat supplies answer, timing and model; the mail stays the question).
Mail access goes through the bot's Outlook COM helper (--helper, default below); nothing here
talks to Outlook directly.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import os
import re
import sys

# -I leaves the script's own folder off sys.path; successcase.py sits next to this file.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import successcase as sc  # noqa: E402

DEFAULT_HELPER = r"C:\Development\birdybot\bot\services\outlook.py"


class HelperError(Exception):
    pass


def load_helper(path: str):
    """Import the Outlook helper from its file path; HelperError with a readable reason otherwise."""
    if not os.path.isfile(path):
        raise HelperError(f"Outlook-helper niet gevonden: {path} (geef het pad met --helper)")
    try:
        spec = importlib.util.spec_from_file_location("vibemodel_outlook_helper", path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
    except Exception as e:  # any import-time failure of a foreign module
        raise HelperError(f"Outlook-helper kon niet geladen worden ({path}): {type(e).__name__}: {e}") from None
    if not getattr(mod, "COM_AVAILABLE", False):
        raise HelperError("pywin32 ontbreekt voor deze Python - installeer met: python -m pip install pywin32")
    for name in ("search_emails", "read_email", "read_inbox"):
        if not callable(getattr(mod, name, None)):
            raise HelperError(f"Outlook-helper {path} heeft geen functie '{name}' - andere versie?")
    return mod


def call(helper, fn: str, *args):
    try:
        return getattr(helper, fn)(*args)
    except Exception as e:
        raise HelperError(f"Outlook gaf een fout bij {fn}: {e}") from None


def mail_key(mail: dict) -> str:
    raw = f"{mail.get('subject', '')}|{mail.get('date', '')}|{mail.get('from_email') or mail.get('from', '')}"
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


QUOTED_HISTORY_RE = re.compile(
    r"^(?:(?:Van|From):\s|-{2,}\s*(?:Original|Oorspronkelijk)|(?:Op|On)\s.*(?:schreef|wrote).*:\s*$)",
    re.I | re.M,
)


def clean_body(body: str) -> str:
    """Mail text without the quoted reply history (unless that is all there is) and blank-line runs."""
    text = "\n".join(ln.rstrip() for ln in (body or "").replace("\r\n", "\n").replace("\r", "\n").split("\n"))
    m = QUOTED_HISTORY_RE.search(text)
    # A forward ("Nu de juiste link.") carries the real question below the header - keep it then.
    if m and len(re.sub(r"\s+", "", text[:m.start()])) >= 40:
        text = text[:m.start()]
    return re.sub(r"\n{3,}", "\n\n", text).strip()


def mail_case(mail: dict) -> dict:
    body = clean_body(mail.get("body") or "")
    return {
        "mail": {"key": mail_key(mail), "subject": mail.get("subject", ""), "date": mail.get("date", "")},
        "wie": mail.get("from") or mail.get("from_email") or "",
        "date": (mail.get("date") or "")[:10],
        "question": body or f"(lege mail) {mail.get('subject', '')}",
        "slug_source": mail.get("subject") or body[:60],
    }


def with_chat(case: dict, chat_arg: str | None, logs_dir: str) -> dict:
    """Overlay what a chat log supplies (answer, timing, model) without touching the mail question."""
    if not chat_arg:
        return case
    chat = sc.build_chat_case(sc.load_chat(sc.resolve_chat_args([chat_arg])[0]), logs_dir)
    merged = dict(chat)
    for k in ("mail", "wie", "date", "question", "slug_source"):
        if case.get(k):
            merged[k] = case[k]
    return merged


def fetch_by_subject(helper, subject: str) -> list[dict]:
    mail = call(helper, "read_email", subject)
    if not mail:
        raise HelperError(f"Geen mail gevonden met '{subject}' in het onderwerp (helper doorzoekt de nieuwste 200).")
    return [mail]


def fetch_since(helper, since: dt.date, query: str | None, scan: int) -> list[dict]:
    mails = []
    for m in call(helper, "read_inbox", scan):
        try:
            day = dt.date.fromisoformat((m.get("date") or "")[:10])
        except ValueError:
            continue
        if day < since:
            continue
        hay = f"{m.get('subject', '')} {m.get('from', '')}".lower()
        if query and query.lower() not in hay:
            continue
        mails.append(m)
    full = []
    for m in mails:
        # read_inbox only has a preview; read_email returns the full body (newest subject match).
        body = call(helper, "read_email", m.get("subject") or "")
        if body and body.get("date") == m.get("date"):
            full.append(body)
        else:
            full.append({**m, "body": m.get("preview", ""), "body_is_preview": True})
    return full


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Outlook-mails -> successcase-bestanden")
    p.add_argument("subject", nargs="?", help="(deel van) het onderwerp van de mail")
    p.add_argument("--since", type=dt.date.fromisoformat, help="alle mails vanaf deze datum (JJJJ-MM-DD)")
    p.add_argument("--query", help="met --since: alleen mails met deze tekst in onderwerp of afzender")
    p.add_argument("--scan", type=int, default=200, help="met --since: aantal nieuwste inboxmails doorzoeken")
    p.add_argument("--chat", help="chatlog (pad of id) om aan de case te koppelen")
    p.add_argument("--case", help="bestaand casebestand om --chat aan te koppelen (zonder Outlook)")
    p.add_argument("--dry-run", action="store_true", help="alleen tonen welke mails een case zouden worden")
    p.add_argument("--out", default=sc.DEFAULT_OUT)
    p.add_argument("--logs", default=sc.DEFAULT_LOGS)
    p.add_argument("--helper", default=os.environ.get("VIBEMODEL_OUTLOOK_HELPER", DEFAULT_HELPER))
    a = p.parse_args(argv)

    if a.case:
        if not a.chat:
            p.error("--case vraagt om --chat")
        if not os.path.isfile(a.case):
            p.error(f"casebestand niet gevonden: {a.case}")
        with open(a.case, encoding="utf-8") as f:
            existing = sc.parse_case(f.read())
        # The mail stays the question; without a mail the chat supplies everything.
        base = {k: existing.get(k) for k in ("mail", "question", "wie", "date")} if existing.get("mail") else {}
        generated = with_chat(base, a.chat, a.logs)
        print(f"case: {sc.write_case(a.out, generated, existing_path=a.case)}")
        print(f"index: {sc.write_index(a.out)}")
        return 0
    if not a.subject and not a.since:
        p.error("geef een onderwerp, --since JJJJ-MM-DD, of --case <bestand> --chat <chatlog>")
    if a.chat and not a.subject:
        p.error("--chat koppelen kan alleen bij één mail (onderwerp) of met --case")

    try:
        helper = load_helper(a.helper)
        mails = fetch_by_subject(helper, a.subject) if a.subject else fetch_since(helper, a.since, a.query, a.scan)
    except HelperError as e:
        print(f"FOUT: {e}", file=sys.stderr)
        return 2

    if not mails:
        print("Geen passende mails gevonden.")
        return 0
    for m in mails:
        note = " (alleen preview, volledige tekst niet gevonden)" if m.get("body_is_preview") else ""
        if a.dry_run:
            print(f"zou case maken: {m.get('date')} | {m.get('from')} | {m.get('subject')}{note}")
            continue
        case = with_chat(mail_case(m), a.chat, a.logs)
        print(f"case: {sc.write_case(a.out, case)}{note}")
    if not a.dry_run:
        print(f"index: {sc.write_index(a.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
