# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Data construction checks; no network, installed data or user settings used."""
from decimal import Decimal
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_lexicon", ROOT / "scripts" / "prepare_lexicon.py")
PREP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREP)


def row(word, syllables, frequency=10):
    return word, Decimal(frequency), 0, tuple(syllables.split())


class DataTests(unittest.TestCase):
    def test_general_parser_validates_weights_and_uses_max_duplicate(self):
        self.assertEqual(PREP.parse_general_dictionary("你好 5 n\n你好 3 v\n".encode()), {"你好": 5})
        for line in ("你好 0 n", "你好 -1 n", "你好 nan n", "你好 5", "你好 1000000000001 n"):
            with self.assertRaises(ValueError):
                PREP.parse_general_dictionary(line.encode())

    def test_same_reading_across_different_segments_is_not_ambiguous(self):
        known = PREP.known_readings([row("你", "ni"), row("好", "hao"), row("你好", "ni hao")])
        self.assertEqual(PREP.infer_unique_reading("你好", known, 2), {("ni", "hao")})

    def test_phrase_only_polyphony_also_prevents_guessing(self):
        known = PREP.known_readings([row("行", "xing"), row("银行", "yin hang"), row("好", "hao")])
        self.assertEqual(len(PREP.infer_unique_reading("行好", known, 2)), 2)

    def test_unknown_character_rejects_incomplete_reading(self):
        known = PREP.known_readings([row("你", "ni")])
        self.assertFalse(PREP.infer_unique_reading("你好", known, 1))

    def test_many_polyphonic_paths_keep_only_two_witnesses(self):
        known = PREP.known_readings([row("行", "xing"), row("行", "hang")])
        self.assertEqual(len(PREP.infer_unique_reading("行" * 12, known, 1)), 2)

    def test_existing_pronunciation_and_weight_remain_unchanged(self):
        old = [row("你", "ni", 20), row("好", "hao", 10), row("你好", "ni hao", 1)]
        additions, stats, _ = PREP.supplement(old, {"你好": 99999})
        self.assertFalse(additions)
        self.assertEqual(stats["existing_word_preserved"], 1)
        self.assertEqual(PREP.prepare(old)[0][("nihao", "你好")], 1)

    def test_frequency_floor_scale_size_and_tie_are_deterministic(self):
        old = [row("你", "ni", 20), row("好", "hao", 10), row("吗", "ma", 5)]
        general = {"好吗": 7, "你好": 7, "好好": 4}
        additions, stats, ratio = PREP.supplement(old, general, 5, 1)
        self.assertEqual(len(additions), 1)
        self.assertEqual(additions[0][0], "你好")
        self.assertEqual(additions[0][1], Decimal(7) * Decimal(35) / Decimal(18))
        self.assertEqual(stats["below_frequency_floor"], 1)
        self.assertEqual(stats["over_size_limit"], 1)
        self.assertEqual(PREP.supplement(old, dict(reversed(list(general.items()))), 5, 1), (additions, stats, ratio))

    def test_non_han_and_long_words_are_out_of_scope(self):
        old = [row("好", "hao")]
        additions, stats, _ = PREP.supplement(old, {"好" * 13: 50, "好A": 50})
        self.assertFalse(additions)
        self.assertEqual(stats["unsupported_length_or_characters"], 2)

    def test_pinned_source_mismatch_is_rejected_without_network(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "dict.txt").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
                PREP.read_general_sources(path, False)


if __name__ == "__main__":
    unittest.main()
