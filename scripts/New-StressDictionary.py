#!/usr/bin/env python3
"""Regenerates AI/dictionaries/ru-stress-full.txt.gz for the neural voice.

Usage: python scripts/New-StressDictionary.py <accents.json.gz> [yo_words.json.gz]

The inputs are the RUAccent dictionaries (MIT License), downloadable once by
the maintainer from Hugging Face:
https://huggingface.co/ruaccent/accentuator -> dictionary/accents.json.gz and
yo_words.json.gz. The script itself is offline and never accesses the network.

Output formats (sorted TSV, gzipped):
- ru-stress-full.txt.gz: "word<TAB>vowel-index" per word form; the index is
  the 0-based position of the stressed vowel. Words containing "ё" are dropped
  because "ё" already marks the stress unambiguously.
- ru-yo-words.txt.gz (when yo_words.json.gz is given): "word-without-ё<TAB>
  word-with-ё". Machine translation writes Russian without "ё", which loses
  the stress hints; the spoken text restores "ё" before synthesis.

Known single-entry errors in the upstream dictionary are corrected in
OVERRIDES below; the browser test suite pins the corrected words, so a future
regeneration that loses a fix fails the tests instead of shipping bad speech.
"""

import gzip
import json
import os
import sys

VOWELS = set("аеёиоуыэюя")
LOWER_RUSSIAN = set("абвгдежзийклмнопрстуфхцчшщъыьэюя")

# Verified corrections of upstream entries that contradict the rest of the
# word family: готова/готово/готовить all stress the second "о", so the bare
# "готов" must be готОв, and the genitive "утра" is утрА (as in «десять утра»).
OVERRIDES = {
    "готов": 1,
    "утра": 1,
}


def parse_entry(key: str, value: str) -> int | None:
    if not key or not value or "+" not in value:
        return None
    if value.replace("+", "") != key:
        return None
    stressed = None
    count = 0
    for ch in value:
        if ch == "+":
            stressed = count
            continue
        if ch in VOWELS:
            count += 1
    if stressed is None or stressed >= count:
        return None
    return stressed


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print(__doc__)
        return 2
    source_path = sys.argv[1]
    yo_path = sys.argv[2] if len(sys.argv) == 3 else ""
    with gzip.open(source_path, "rt", encoding="utf-8") as f:
        raw = json.load(f)

    entries: dict[str, int] = {}
    skipped = 0
    for key, value in raw.items():
        index = parse_entry(key, value)
        if index is None or not key or not set(key) <= LOWER_RUSSIAN:
            skipped += 1
            continue
        previous = entries.get(key)
        if previous is None or index < previous:
            entries[key] = index

    applied = []
    for key, index in OVERRIDES.items():
        if key not in entries:
            entries[key] = index
            applied.append(key + " (added)")
        elif entries[key] != index:
            entries[key] = index
            applied.append(key)

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(root, "src", "NexusMonach", "AI", "dictionaries", "ru-stress-full.txt.gz")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with gzip.open(target, "wt", encoding="utf-8", newline="\n") as out:
        for key in sorted(entries):
            out.write(f"{key}\t{entries[key]}\n")

    print(f"entries: {len(entries)} | skipped: {skipped} | overrides applied: {applied}")
    print(f"written: {target} ({os.path.getsize(target)} bytes)")

    if not yo_path:
        return 0
    with gzip.open(yo_path, "rt", encoding="utf-8") as f:
        yo_raw = json.load(f)
    yo_entries = {
        key.strip(): value.strip()
        for key, value in yo_raw.items()
        if key.strip() and value.strip() and "ё" in value and "ё" not in key
        and set(key.strip()) <= LOWER_RUSSIAN
    }
    yo_target = os.path.join(root, "src", "NexusMonach", "AI", "dictionaries",
                             "ru-yo-words.txt.gz")
    with gzip.open(yo_target, "wt", encoding="utf-8", newline="\n") as out:
        for key in sorted(yo_entries):
            out.write(f"{key}\t{yo_entries[key]}\n")
    print(f"yo entries: {len(yo_entries)}")
    print(f"written: {yo_target} ({os.path.getsize(yo_target)} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
