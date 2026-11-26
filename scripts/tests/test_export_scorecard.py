"""The scorecard counts a synthetic export the same way every time.

    python -I -m unittest discover -s scripts/tests -v
"""

import io
import json
import os
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import export_scorecard as es  # noqa: E402

URL = "http://127.0.0.1:18884"

CHAT_NL = f"""# Muur plaatsen

- Datum: 2026-11-18 15:42 – 2026-11-18 15:44
- Project: Testproject
- Backend: claude-cli · model: claude-opus-5-5
- Chat: 20261118-154222-aaaa · beurten: 2 · commands: 6 (3 mislukt)

## Beurt 1 — 15:42:22

**Vraag**

Plaats een muur zoals aan de andere kant

**Commands** (4, 2 mislukt)

| # | command | args | resultaat | duur |
|---|---|---|---|---|
| 1 | exec | curl.exe -s -G "{URL}/exec" --data-urlencode "args=var w = doc.GetElement(x) as Wall;" | ✗ Exit code 3 | 3.1 s |
| 2 | exec | $f="$env:TEMP\\vm_q.cs"; curl.exe -s -G "{URL}/exec" --data-urlencode "args@$f" | ✓ | 3.2 s |
| 3 | exec | $f="$env:TEMP\\vm_q.cs"; (Get-Content $f -Raw).Replace('e.Category?.Name','x') \\| Set-Content $f | ✓ | 3.0 s |
| 4 | sheetinfo | curl.exe -s "{URL}/sheetinfo" \\| Select-Object -First 5 | ✗ Exit code 255 | 1.0 s |

Volledige argumenten van stap 2 (exec):

```csharp
$f="$env:TEMP\\vm_q.cs"; @'
var n = e.Category?.Name;
'@ | Set-Content $f; curl.exe -s -G "{URL}/exec" --data-urlencode "args@$f"
```

Volledige argumenten van stap 3 (exec):

```csharp
$f="$env:TEMP\\vm_q.cs"; (Get-Content $f -Raw).Replace('e.Category?.Name','(e.Category==null?"":e.Category.Name)') | Set-Content $f; curl.exe -s -G "{URL}/exec" --data-urlencode "args@$f"; curl.exe -s "{URL}/info"
```

**Antwoord** (29 s)

Gedaan.

Zal ik ook de andere kant doen?

_1.2M tokens · gesprekslengte 49.4k tokens · ≈ € 0,33_

## Beurt 2 — 15:43:39

**Vraag**

Je hebt me niet begrepen, de hoogtes kloppen niet

**Commands** (2, 1 mislukt)

| # | command | args | resultaat | duur |
|---|---|---|---|---|
| 1 | info | curl.exe -s -m 20 "{URL}/info" | ✗ Exit code 28 | 20 s |
| 2 | wall | curl.exe -s "{URL}/wall?args=0%200%205000%200" | ✓ | 1.0 s |

**Antwoord** (12 s)

De muur staat er nu.

_300k tokens · gesprekslengte 60.0k tokens_

_VibeModel 1.2.22 · modus: ask · pakket: none_
"""

CHAT_OLD_EN = """# Old chat

- Date: 2026-10-12 10:00 – 2026-10-12 10:05

## Turn 1 — 10:00:00

**Question**

How do I hide a category?

**Commands**

_no commands recorded — a chat from before this version, and debug logging was off_

**Answer**

Use Visibility/Graphics.

"""


class ScorecardTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = os.path.join(self.tmp.name, "export")
        os.makedirs(os.path.join(self.dir, "chats"))
        for name, text in (("20261118-1542 muur.md", CHAT_NL), ("20261012-1000 old.md", CHAT_OLD_EN)):
            with io.open(os.path.join(self.dir, "chats", name), "w", encoding="utf-8") as f:
                f.write(text)
        with io.open(os.path.join(self.dir, "INDEX.md"), "w", encoding="utf-8") as f:
            f.write("# index\n")

    def tearDown(self):
        self.tmp.cleanup()

    def card(self, *sources):
        chats = [es.parse_chat(n, t) for n, t in es.read_sources(list(sources))]
        return es.score(chats)

    def test_counts(self):
        c = self.card(self.dir)
        self.assertEqual(c["countingVersion"], es.COUNTING_VERSION)
        self.assertEqual(c["chats"], 2)
        self.assertEqual(c["chatsWithRecordedCommands"], 1)
        self.assertEqual(c["turns"], 3)
        self.assertEqual(c["turnsWithRecordedCommands"], 2)
        self.assertEqual(c["commands"], 6)
        self.assertEqual(c["failed"], 3)
        self.assertEqual(c["failedExitCodeOnly"], 3)

    def test_failure_classes(self):
        c = self.card(self.dir)
        self.assertEqual(c["failureClasses"], {"PowerShell inline": 1, "pipe": 1, "timeout": 1})

    def test_hidden_failure_is_a_compile_fix_patch(self):
        # Step 2 sent `?.`; step 3 patched exactly that out of the same file: step 2 failed unseen.
        self.assertEqual(self.card(self.dir)["hiddenFailuresHeuristic"], 1)

    def test_dry_run_then_real_run_is_no_hidden_failure(self):
        steps = [{"command": "exec", "ok": True, "fullArgs": URL + "/exec?readonly=1 " + "x" * 80, "args": ""},
                 {"command": "exec", "ok": True, "fullArgs": URL + "/exec " + "x" * 80, "args": ""}]
        self.assertEqual(es.hidden_failures(steps), 0)

    def test_several_server_calls_in_one_step(self):
        self.assertEqual(self.card(self.dir)["stepsWithSeveralServerCalls"], 1)

    def test_corrections_offers_tokens(self):
        c = self.card(self.dir)
        self.assertEqual(c["followUpTurns"], 1)
        self.assertEqual(c["correctionTurnsHeuristic"], 1)
        self.assertEqual(c["answersOfferingNextStep"], 1)
        self.assertEqual(c["answersEndingOnQuestion"], 1)
        self.assertEqual(c["medianTokensPerTurn"], 750000)
        self.assertEqual(c["peakContext"], 60000)

    def test_build_mode_pack_line(self):
        self.assertEqual(self.card(self.dir)["builds"], [{"version": "1.2.22", "mode": "ask", "pack": "none"}])

    def test_zip_and_single_file_give_the_same_numbers(self):
        z = os.path.join(self.tmp.name, "chats.zip")
        with zipfile.ZipFile(z, "w") as zf:
            for name in os.listdir(os.path.join(self.dir, "chats")):
                zf.write(os.path.join(self.dir, "chats", name), "chats/" + name)
            zf.writestr("README.md", "# readme\n")
        single = os.path.join(self.tmp.name, "chats-1.md")
        with io.open(single, "w", encoding="utf-8") as f:
            f.write("# README\n\ntext\n\n---\n\n" + CHAT_NL + "\n---\n\n" + CHAT_OLD_EN)
        self.assertEqual(self.card(z), self.card(self.dir))
        self.assertEqual(self.card(single), self.card(self.dir))

    def test_a_markdown_rule_inside_an_answer_does_not_split_the_chat(self):
        with_rule = CHAT_NL.replace("Gedaan.\n", "Gedaan.\n\n---\n\nNog een alinea.\n")
        single = os.path.join(self.tmp.name, "chats-2.md")
        with io.open(single, "w", encoding="utf-8") as f:
            f.write("# README\n\ntext\n\n---\n\n" + with_rule + "\n---\n\n" + CHAT_OLD_EN)
        c = self.card(single)
        self.assertEqual((c["chats"], c["turns"], c["commands"]), (2, 3, 6))
        self.assertEqual(c["answersOfferingNextStep"], 1)

    def test_raw_chat_json(self):
        path = os.path.join(self.tmp.name, "chat.json")
        with io.open(path, "w", encoding="utf-8") as f:
            json.dump({"id": "x", "title": "t", "messages": [
                {"role": "user", "content": "maak een muur", "timestampUtc": "2026-11-18T10:00:00Z"},
                {"role": "assistant", "content": "Klaar.", "timestampUtc": "2026-11-18T10:00:10Z",
                 "steps": [{"command": "wall", "args": "0 0 5000 0", "ok": True},
                           {"command": "exec", "args": "x", "ok": False, "error": "Exit code 3"}],
                 "usage": {"inputTokens": 1000, "outputTokens": 500, "contextTokens": 900}},
            ]}, f)
        c = self.card(path)
        self.assertEqual((c["turns"], c["commands"], c["failed"], c["medianTokensPerTurn"]), (1, 2, 1, 1500))

    def test_main_writes_md_and_json(self):
        base = os.path.join(self.tmp.name, "card")
        out = io.StringIO()
        old = sys.stdout
        sys.stdout = out
        try:
            self.assertEqual(es.main([self.dir, "--out", base]), 0)
        finally:
            sys.stdout = old
        with io.open(base + ".json", encoding="utf-8") as f:
            self.assertEqual(json.load(f)["commands"], 6)
        with io.open(base + ".md", encoding="utf-8") as f:
            self.assertIn("counting version", f.read())


if __name__ == "__main__":
    unittest.main()
