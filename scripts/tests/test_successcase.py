"""Template merge: hand-filled fields survive a rerun, generated parts are refreshed.

    python -I -m unittest discover -s scripts/tests -v
"""

import json
import os
import re
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import successcase as sc  # noqa: E402
import successcase_mail as scm  # noqa: E402


def msg(role, content, stamp):
    return {"role": role, "content": content, "timestampUtc": f"2026-10-12T{stamp}.1234567Z"}


class TemplateMergeTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.out = os.path.join(self.tmp.name, "cases")
        self.logs = os.path.join(self.tmp.name, "logs")  # no log -> commands n.b.
        self.chat_path = os.path.join(self.tmp.name, "20261012-101908-abcd.json")
        self.messages = [
            msg("user", "Ik zie de hoeksteen niet in 3D", "10:19:08"),
            msg("assistant", "Let me look.\n\nOpgelost: het filter stond uit.", "10:20:38"),
        ]
        self.save_chat()

    def tearDown(self):
        self.tmp.cleanup()

    def save_chat(self):
        with open(self.chat_path, "w", encoding="utf-8") as f:
            json.dump({"id": "20261012-101908-abcd", "title": "Hoeksteen", "project": "kademuur",
                       "messages": self.messages}, f)

    def run_chat(self):
        return sc.write_case(self.out, sc.build_chat_case(sc.load_chat(self.chat_path), self.logs))

    def read(self, path):
        with open(path, encoding="utf-8") as f:
            return f.read()

    def fill_by_hand(self, path):
        text = self.read(path)
        text = text.replace("- **Wie:**", "- **Wie:** Anna")
        text = text.replace("Score (1-5):", "Score (1-5): 4")
        text = text.replace(sc.OPMERKINGEN_PLACEHOLDER, "Werkte, maar de view moest\nnog handmatig bijgesteld.")
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)

    def test_first_run_renders_template(self):
        text = self.read(self.run_chat())
        for heading in ("Vraag", "Antwoord", "Verloop", "Totale tijd tot oplossing", "Kwaliteit",
                        "Opmerkingen", "Chatlog"):
            self.assertIn(f"## {heading}", text)
        self.assertIn("- **Case nr:** 1", text)
        self.assertIn("- **Model:** kademuur", text)
        self.assertIn("> Ik zie de hoeksteen niet in 3D", text)
        self.assertIn("Opgelost: het filter stond uit.", text)
        self.assertNotIn("Let me look.", text)  # progress note skipped
        self.assertIn("| 1 | ", text)
        self.assertIn("1m30s", text)
        self.assertIn(sc.QUALITY_SCALE, text)

    def test_hand_fields_survive_rerun_and_generated_parts_refresh(self):
        path = self.run_chat()
        self.fill_by_hand(path)
        self.messages += [msg("user", "En nu de dilatatie", "10:25:00"),
                          msg("assistant", "Dilatatie loopt nu door.", "10:25:30")]
        self.save_chat()

        self.assertEqual(self.run_chat(), path)  # same file, no duplicate
        text = self.read(path)
        info = sc.parse_case(text)
        self.assertEqual(info["wie"], "Anna")
        self.assertEqual(info["score"], "4")
        self.assertEqual(info["opmerkingen"], "Werkte, maar de view moest\nnog handmatig bijgesteld.")
        self.assertEqual(info["nr"], 1)
        self.assertIn("Dilatatie loopt nu door.", text)  # answer refreshed to the new last reply
        self.assertIn("| 2 | ", text)  # new turn in Verloop
        self.assertEqual(len([n for n in os.listdir(self.out) if n != "INDEX.md"]), 1)

        # A third, unchanged run is byte-identical: idempotent.
        self.run_chat()
        self.assertEqual(self.read(path), text)

    def test_mail_question_and_sender_survive_chat_rerun(self):
        mail = {"subject": "RE: hoeksteen", "date": "2026-10-12 09:42", "from": "Jansen, Anna",
                "body": "Hoi Piet,\r\n\r\n\r\nIk krijg de hoeksteen niet geplaatst bij de L-wand, kun je helpen?\r\n"
                        "\r\nVan: Bakker, Piet\r\nVerzonden: vrijdag\r\n\r\nHallo allen"}
        path = sc.write_case(self.out, scm.with_chat(scm.mail_case(mail), self.chat_path, self.logs))
        text = self.read(path)
        self.assertIn("- **Wie:** Jansen, Anna", text)
        self.assertIn("> Ik krijg de hoeksteen niet geplaatst", text)
        self.assertNotIn("Hallo allen", text)  # quoted history stripped
        self.assertIn("Opgelost: het filter stond uit.", text)

        self.run_chat()  # chat-only rerun must keep the mail as the question
        info = sc.parse_case(self.read(path))
        self.assertIn("Ik krijg de hoeksteen niet geplaatst", info["question"])
        self.assertEqual(info["wie"], "Jansen, Anna")
        self.assertEqual(info["mail"]["subject"], "RE: hoeksteen")

    def test_link_chat_to_mail_case_offline_keeps_mail_question(self):
        mail = {"subject": "Plotten", "date": "2026-10-12 14:31", "from": "Test, Persoon",
                "body": "Hoe plot ik sheet 0031 naar PDF op A0?"}
        path = sc.write_case(self.out, scm.mail_case(mail))
        self.fill_by_hand(path)
        rc = scm.main(["--case", path, "--chat", self.chat_path, "--out", self.out, "--logs", self.logs])
        self.assertEqual(rc, 0)
        info = sc.parse_case(self.read(path))
        self.assertEqual(info["question"], "Hoe plot ik sheet 0031 naar PDF op A0?")
        self.assertEqual(info["chat"], "20261012-101908-abcd")
        self.assertEqual(info["score"], "4")
        self.assertIn("Opgelost: het filter stond uit.", self.read(path))

    def test_index_lists_cases_with_quality(self):
        path = self.run_chat()
        self.fill_by_hand(path)
        index = self.read(sc.write_index(self.out))
        row = [ln for ln in index.splitlines() if ln.startswith("| [1]")][0]
        self.assertIn("kademuur", row)
        self.assertRegex(row, r"\| 1m30s \(werk 1m30s\) \| 4 \|$")
        self.assertIn("gemiddelde kwaliteit 4.0", index)

    def test_missing_helper_is_a_clear_error(self):
        with self.assertRaises(scm.HelperError) as ctx:
            scm.load_helper(os.path.join(self.tmp.name, "nope.py"))
        self.assertIn("niet gevonden", str(ctx.exception))
        broken = os.path.join(self.tmp.name, "broken.py")
        with open(broken, "w") as f:
            f.write("import does_not_exist_xyz\n")
        with self.assertRaises(scm.HelperError):
            scm.load_helper(broken)

    def test_command_count_from_debug_log(self):
        os.makedirs(self.logs)
        start = sc.local(sc.parse_utc("2026-10-12T10:19:08Z"))
        inside = start.replace(tzinfo=None)
        lines = [
            f"[{inside:%H:%M:%S}.500] [INFO] Executing: context ",
            f"[{inside:%H:%M}:30.000] [INFO] Executing batch: 3 commands",
            "[23:59:59.000] [INFO] Executing: info ",  # outside the turn
        ]
        with open(os.path.join(self.logs, f"log_{inside:%Y-%m-%d}.txt"), "w") as f:
            f.write("\n".join(lines) + "\n")
        case = sc.build_chat_case(sc.load_chat(self.chat_path), self.logs)
        self.assertEqual(case["turns"][0]["commands"], 4)


if __name__ == "__main__":
    unittest.main()
