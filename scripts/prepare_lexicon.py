#!/usr/bin/env python3
# Copyright (c) 2026 Mansur
# SPDX-License-Identifier: MIT
"""Build a general lexicon from pinned AOSP pronunciation and jieba word data.

Output: compact spelling<TAB>Chinese<TAB>positive weight (UTF-8, LF).
syllables.tsv additionally preserves boundaries for explicit apostrophe matching.
No third-party input/segmentation engine is imported. Only unambiguous AOSP
readings are used to annotate new words. Run --download once, then offline.
"""
from __future__ import annotations

import argparse
import base64
from collections import Counter
from decimal import Decimal, InvalidOperation
import hashlib
import itertools
import json
from pathlib import Path
import re
import urllib.request

REVISION = "49aebad1c1cfbbcaa9288ffed5161e79e57c3679"
UPSTREAM = "https://android.googlesource.com/platform/packages/inputmethods/PinyinIME"
SOURCE_FILES = {
    "rawdict_utf16_65105_freq.txt": (
        "jni/data/rawdict_utf16_65105_freq.txt",
        "408700f28a56091fa07f3b849a0f134fbfc71e6b2ae9b3f52973a5b076f599ff",
    ),
    "NOTICE": ("NOTICE", "b5830d96fb5a7e7e7ebcc295f352846b4b998e78fdc8f9aa68e134d2e4b39986"),
    "MODULE_LICENSE_APACHE2": (
        "MODULE_LICENSE_APACHE2",
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    ),
}
SYLLABLE = re.compile(r"[a-z]{1,6}\Z")
JIEBA_REVISION = "67fa2e36e72f69d9134b8a1037b83fbb070b9775"
JIEBA_UPSTREAM = "https://github.com/fxsjy/jieba"
JIEBA_FILES = {
    "dict.txt": ("jieba/dict.txt", "7197c3211ddd98962b036cdf40324d1ea2bfaa12bd028e68faa70111a88e12a8"),
    "LICENSE": ("LICENSE", "18ba0984839f85853b29fadaf992f7dba8fd0ca0fbeae34de2b8735222dc7a37"),
}
# Bound both the long tail and the added index size. These are corpus-wide
# rules, never a list of preferred words. Keep ample room below the binary
# decoder's 300,000-entry limit, including up to 10,000 personal entries.
MIN_GENERAL_FREQUENCY = 5
MAX_GENERAL_WORDS = 80000
MAX_GENERAL_CHARACTERS = 12


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_sources(directory: Path, download: bool) -> dict[str, bytes]:
    contents = {}
    for name, (relative, expected) in SOURCE_FILES.items():
        path = directory / name
        if not path.is_file():
            if not download:
                raise ValueError(f"Missing {path}. Run once with --download.")
            url = f"{UPSTREAM}/+/{REVISION}/{relative}?format=TEXT"
            request = urllib.request.Request(url, headers={"User-Agent": "MansurIME-data-builder/1"})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = base64.b64decode(response.read().strip(), validate=True)
            if digest(data) != expected:
                raise ValueError(f"Upstream SHA256 mismatch for {name}; nothing was installed.")
            directory.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        data = path.read_bytes()
        if digest(data) != expected:
            raise ValueError(f"SHA256 mismatch for {path}; refusing changed source data.")
        contents[name] = data
    return contents


def parse_dictionary(data: bytes) -> list[tuple[str, Decimal, int, tuple[str, ...]]]:
    if not data.startswith(b"\xff\xfe"):
        raise ValueError("Expected UTF-16 little-endian BOM.")
    records = []
    for number, line in enumerate(data.decode("utf-16").splitlines(), 1):
        if not line.strip():
            continue
        fields = line.split()
        if len(fields) < 4:
            raise ValueError(f"Line {number}: missing fields.")
        word, raw_frequency, raw_gbk, *syllables = fields
        try:
            frequency = Decimal(raw_frequency)
        except InvalidOperation as error:
            raise ValueError(f"Line {number}: invalid weight.") from error
        if not frequency.is_finite() or frequency <= 0:
            raise ValueError(f"Line {number}: weight must be finite and positive.")
        if raw_gbk not in {"0", "1"}:
            raise ValueError(f"Line {number}: unknown GBK marker.")
        if len(syllables) != len(word) or not all(SYLLABLE.fullmatch(s) for s in syllables):
            raise ValueError(f"Line {number}: invalid spelling or character/syllable count.")
        if not all("\u3400" <= character <= "\u9fff" or character == "〇" for character in word):
            raise ValueError(f"Line {number}: unexpected character repertoire.")
        records.append((word, frequency, int(raw_gbk), tuple(syllables)))
    if not records:
        raise ValueError("Dictionary is empty.")
    return records


def read_general_sources(directory: Path, download: bool) -> dict[str, bytes]:
    contents = {}
    for name, (relative, expected) in JIEBA_FILES.items():
        path = directory / name
        if not path.is_file():
            if not download:
                raise ValueError(f"Missing {path}. Run once with --download.")
            url = f"https://raw.githubusercontent.com/fxsjy/jieba/{JIEBA_REVISION}/{relative}"
            request = urllib.request.Request(url, headers={"User-Agent": "MansurIME-data-builder/1"})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = response.read(16 * 1024 * 1024 + 1)
            if digest(data) != expected:
                raise ValueError(f"Upstream SHA256 mismatch for jieba {name}; nothing was installed.")
            directory.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        data = path.read_bytes()
        if digest(data) != expected:
            raise ValueError(f"SHA256 mismatch for {path}; refusing changed source data.")
        contents[name] = data
    return contents


def parse_general_dictionary(data: bytes) -> dict[str, int]:
    words = {}
    for number, line in enumerate(data.decode("utf-8").splitlines(), 1):
        fields = line.split()
        if len(fields) != 3 or not re.fullmatch(r"[0-9]+", fields[1]):
            raise ValueError(f"General dictionary line {number}: invalid fields.")
        word, frequency, _part_of_speech = fields
        value = int(frequency)
        if not word or not 0 < value <= 10**12:
            raise ValueError(f"General dictionary line {number}: invalid weight.")
        words[word] = max(words.get(word, 0), value)
    if not words:
        raise ValueError("General dictionary is empty.")
    return words


def known_readings(records):
    known = {}
    for word, _frequency, _gbk, syllables in records:
        known.setdefault(word, set()).add(syllables)
        # Polyphony can appear only inside a source phrase even if a standalone
        # character entry lists one reading. Observe every aligned occurrence.
        for character, syllable in zip(word, syllables):
            known.setdefault(character, set()).add((syllable,))
    # Deterministic traversal; two alternatives are enough to reject ambiguity.
    return {word: tuple(sorted(readings)) for word, readings in known.items()}


def infer_unique_reading(word: str, known: dict, maximum_segment: int):
    """Return at most two distinct readings; never enumerate their product.

    All source segmentations participate, including single-character readings.
    Different readings of the same prefix can never converge: source syllables
    have one-to-one character alignment. Keeping two witnesses is sufficient
    to prove ambiguity if the remaining suffix can be completed.
    """
    states = [set() for _ in range(len(word) + 1)]
    states[0].add(())
    for start in range(len(word)):
        if not states[start]:
            continue
        for end in range(start + 1, min(start + maximum_segment, len(word)) + 1):
            if len(states[end]) == 2:
                continue
            for syllables in known.get(word[start:end], ()):
                for prefix in sorted(states[start]):
                    if len(states[end]) < 2:
                        states[end].add(prefix + syllables)
    return states[-1]


def supplement(records, general_words, minimum_frequency=MIN_GENERAL_FREQUENCY,
               maximum_words=MAX_GENERAL_WORDS):
    known = known_readings(records)
    maximum_segment = max(map(len, known))
    stats = Counter()
    eligible = []
    for word, frequency in sorted(general_words.items()):
        if word in known:
            stats["existing_word_preserved"] += 1
            continue
        if not 2 <= len(word) <= MAX_GENERAL_CHARACTERS or not all(
                "\u3400" <= c <= "\u9fff" or c == "〇" for c in word):
            stats["unsupported_length_or_characters"] += 1
            continue
        readings = infer_unique_reading(word, known, maximum_segment)
        if not readings:
            stats["no_complete_source_reading"] += 1
            continue
        if len(readings) != 1:
            stats["ambiguous_source_reading"] += 1
            continue
        stats["unique_source_reading"] += 1
        if frequency < minimum_frequency:
            stats["below_frequency_floor"] += 1
            continue
        eligible.append((word, frequency, next(iter(readings))))
    eligible.sort(key=lambda r: (-r[1], r[0]))
    stats["over_size_limit"] = max(0, len(eligible) - maximum_words)
    # Convert source unigram counts to the original corpus' total weight scale.
    # Existing AOSP entries/readings are not overwritten or reweighted.
    ratio = sum(r[1] for r in records) / Decimal(sum(general_words.values()))
    additions = [(word, Decimal(frequency) * ratio, 0, syllables)
                 for word, frequency, syllables in eligible[:maximum_words]]
    stats["added_words"] = len(additions)
    return additions, dict(sorted(stats.items())), ratio


def spelling_variants(syllables: tuple[str, ...]) -> list[tuple[str, ...]]:
    # Source lue/nue also accept lve/nve; nv/lv never become nu/lu.
    choices = [((s, s[0] + "ve") if s in {"lue", "nue"} else (s,)) for s in syllables]
    return list(itertools.product(*choices))


def prepare(records: list[tuple[str, Decimal, int, tuple[str, ...]]]):
    base: dict[tuple[str, str], Decimal] = {}
    detailed: dict[tuple[str, str, tuple[str, ...]], Decimal] = {}
    for word, frequency, _gbk, source_syllables in records:
        for syllables in spelling_variants(source_syllables):
            compact = "".join(syllables)
            key = (compact, word)
            base[key] = max(base.get(key, Decimal(0)), frequency)
            detail_key = (compact, word, syllables)
            detailed[detail_key] = max(detailed.get(detail_key, Decimal(0)), frequency)
    return base, detailed


def decimal_text(value: Decimal) -> str:
    return format(value.normalize(), "f")


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, default=root / "data" / "downloads" / "aosp-pinyin-49aebad")
    parser.add_argument("--general-source-dir", type=Path, default=root / "data" / "downloads" / "jieba-67fa2e36e72f")
    parser.add_argument("--output-dir", type=Path, default=root / "data" / "generated")
    parser.add_argument("--download", action="store_true", help="Fetch missing pinned data files.")
    args = parser.parse_args()
    contents = read_sources(args.source_dir, args.download)
    records = parse_dictionary(contents["rawdict_utf16_65105_freq.txt"])
    general_contents = read_general_sources(args.general_source_dir, args.download)
    general_words = parse_general_dictionary(general_contents["dict.txt"])
    additions, supplement_stats, frequency_ratio = supplement(records, general_words)
    base, detailed = prepare(records + additions)
    if len(base) > 290000:
        raise ValueError("Generated dictionary leaves insufficient capacity for personal entries.")
    base_bytes = "".join(
        f"{pinyin}\t{word}\t{decimal_text(weight)}\n"
        for (pinyin, word), weight in sorted(base.items())
    ).encode("utf-8")
    detailed_bytes = "".join(
        f"{pinyin}\t{word}\t{decimal_text(weight)}\t{' '.join(syllables)}\n"
        for (pinyin, word, syllables), weight in sorted(detailed.items())
    ).encode("utf-8")
    metadata = {
        "schema_version": 2,
        "dataset": "AOSP pronunciation data with conservative jieba general-word supplement (data only)",
        "license": "Apache-2.0 AND MIT",
        "copyright": "Copyright (c) 2009, The Android Open Source Project",
        "revision": REVISION,
        "repository": UPSTREAM,
        "sources": [
            {"file": name, "url": f"{UPSTREAM}/+/{REVISION}/{relative}?format=TEXT",
             "bytes": len(contents[name]), "sha256": expected}
            for name, (relative, expected) in SOURCE_FILES.items()
        ],
        "source_records": len(records),
        "source_unique_words": len({r[0] for r in records}),
        "source_single_character_records": sum(len(r[0]) == 1 for r in records),
        "source_distinct_single_characters": len({r[0] for r in records if len(r[0]) == 1}),
        "source_word_lengths": dict(sorted(Counter(len(r[0]) for r in records).items())),
        "source_syllable_count": len({s for r in records for s in r[3]}),
        "general_supplement": {
            "dataset": "jieba dict.txt words and unigram frequencies (no engine code)",
            "license": "MIT", "copyright": "Copyright (c) 2013 Sun Junyi",
            "revision": JIEBA_REVISION, "repository": JIEBA_UPSTREAM,
            "sources": [{"file": name,
                         "url": f"https://raw.githubusercontent.com/fxsjy/jieba/{JIEBA_REVISION}/{relative}",
                         "bytes": len(general_contents[name]), "sha256": expected}
                        for name, (relative, expected) in JIEBA_FILES.items()],
            "source_unique_words": len(general_words), "statistics": supplement_stats,
            "pronunciation": "All complete segmentations through pinned AOSP words/characters must agree. Character readings include every aligned occurrence in source words, not just standalone entries. This means unique observed source pronunciation, not a linguistic completeness guarantee. At most two witnesses per prefix; ambiguous or unknown words are skipped. No guessed or Cartesian-expanded readings.",
            "minimum_source_frequency": MIN_GENERAL_FREQUENCY,
            "maximum_added_words": MAX_GENERAL_WORDS,
            "maximum_characters": MAX_GENERAL_CHARACTERS,
            "frequency_scale": decimal_text(frequency_ratio),
            "frequency_policy": "New weights = jieba count * (AOSP total weight / jieba total count). Existing AOSP weights/readings unchanged. Size cap selects descending source frequency, then lexical word order.",
        },
        "conversion": {
            "encoding": "UTF-8 without BOM, LF",
            "gbk_marker": "Validated; both flags retained. Neither is a simplified/traditional flag.",
            "frequency": "Positive decimal retained. Equal (spelling,word) duplicates use max, not sum.",
            "aliases": "Keep nv/lv distinct from nu/lu; also accept nve/lve for source nue/lue.",
            "apostrophe": "No apostrophes in source. syllables.tsv preserves boundaries. User apostrophes must constrain boundaries, never be discarded without checking.",
            "base_columns": ["compact_pinyin", "chinese", "positive_frequency"],
            "syllable_columns": ["compact_pinyin", "chinese", "positive_frequency", "space_separated_syllables"],
        },
        "outputs": {
            "base.tsv": {"records": len(base), "bytes": len(base_bytes), "sha256": digest(base_bytes)},
            "syllables.tsv": {"records": len(detailed), "bytes": len(detailed_bytes), "sha256": digest(detailed_bytes)},
        },
        "limits": ["Conservative general vocabulary supplement; no modern-language completeness claim.",
                   "Unigram weights, not a contextual/bigram language model.",
                   "Ambiguous source pronunciations are deliberately omitted from the supplement, including some common polyphonic words.",
                   "Source-frequency floor and size cap omit the long tail; personal/domain words can be imported separately.",
                   "Original entries contain 1 to 4 characters; new entries at most 12. Longer sentences require our own decoder.",
                   "Includes uncommon/traditional characters; not a simplified-only vocabulary."],
    }
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for name, data in {"base.tsv": base_bytes, "syllables.tsv": detailed_bytes,
                       "NOTICE-AOSP.txt": contents["NOTICE"],
                       "NOTICE-JIEBA.txt": general_contents["LICENSE"],
                       "lexicon-manifest.json": (json.dumps(metadata, ensure_ascii=False, indent=2) + "\n").encode("utf-8")}.items():
        destination = args.output_dir / name
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        temporary.write_bytes(data)
        temporary.replace(destination)
    print(json.dumps({"source_records": len(records), "supplement": supplement_stats,
                      "outputs": metadata["outputs"]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError) as error:
        raise SystemExit(str(error)) from error
