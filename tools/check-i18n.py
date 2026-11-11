#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only

# Usage: python tools/check-i18n.py
# Verifies i18n file consistency: all language files for each table must have identical key sets.
# Two sets of tables are checked: the client's UI tables (src/StudyLife.Client/i18ntext) and the
# server text catalogue (src/StudyLife.Server/Texts, the notification texts the server sends
# itself). For the server catalogue every language additionally has to keep the placeholders
# ({0}, {1:t}, ...) of the English template - a translation that drops or renames one would
# only fail when that user's notification fires.

import json
import re
import sys
from pathlib import Path
from collections import defaultdict


EXPECTED_LANGUAGES = {"bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr",
                      "ga", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt",
                      "ro", "ru", "sk", "sl", "sv", "uk"}
I18N_DIR = Path("src/StudyLife.Client/i18ntext")
SERVER_TEXT_DIR = Path("src/StudyLife.Server/Texts")
PLACEHOLDER = re.compile(r"\{[^{}]*\}")


def discover_tables():
    """Discover all distinct i18n table names from filenames."""
    tables = set()
    for json_file in I18N_DIR.glob("*.json"):
        # Pattern: <TableName>.<lang>.json
        parts = json_file.stem.split(".")
        if len(parts) == 2:
            table_name = parts[0]
            tables.add(table_name)
    return sorted(tables)


def load_keys(file_path):
    """Load JSON keys from file, handling UTF-8 BOM."""
    with open(file_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
        return set(data.keys())


def check_table(table_name):
    """Check a single table for consistency across all languages."""
    files_by_lang = {}
    missing_langs = set()

    # Load all language files for this table
    for lang in EXPECTED_LANGUAGES:
        file_path = I18N_DIR / f"{table_name}.{lang}.json"
        if file_path.exists():
            try:
                keys = load_keys(file_path)
                files_by_lang[lang] = keys
            except Exception as e:
                print(f"ERROR {table_name}: Failed to read {file_path}: {e}")
                return False
        else:
            missing_langs.add(lang)

    # Report missing languages
    if missing_langs:
        print(f"MISMATCH {table_name} — Missing {len(missing_langs)} language file(s):")
        for lang in sorted(missing_langs):
            print(f"  - {lang}")
        return False

    # Check all key sets match
    reference_lang = "en"
    if reference_lang not in files_by_lang:
        print(f"ERROR {table_name}: English (en) file not found")
        return False

    reference_keys = files_by_lang[reference_lang]
    all_match = True
    mismatches = {}

    for lang in sorted(files_by_lang.keys()):
        if lang == reference_lang:
            continue
        lang_keys = files_by_lang[lang]
        if lang_keys != reference_keys:
            all_match = False
            missing = reference_keys - lang_keys
            extra = lang_keys - reference_keys
            mismatches[lang] = {"missing": missing, "extra": extra}

    if all_match:
        key_count = len(reference_keys)
        print(f"OK {table_name} — {len(files_by_lang)} files, {key_count} keys")
        return True
    else:
        print(f"MISMATCH {table_name} — Key set inconsistencies:")
        for lang in sorted(mismatches.keys()):
            missing = mismatches[lang]["missing"]
            extra = mismatches[lang]["extra"]
            if missing or extra:
                print(f"  {lang}:")
                if missing:
                    print(f"    Missing: {sorted(missing)}")
                if extra:
                    print(f"    Extra: {sorted(extra)}")
        return False


def check_server_texts():
    """The server text catalogue: ServerText.<lang>.json, one flat key -> template table per language."""
    if not SERVER_TEXT_DIR.exists():
        print(f"ERROR: server text directory not found: {SERVER_TEXT_DIR}")
        return False

    tables = {}
    for lang in sorted(EXPECTED_LANGUAGES):
        path = SERVER_TEXT_DIR / f"ServerText.{lang}.json"
        if not path.exists():
            print(f"MISMATCH ServerText - missing language file: {path}")
            return False
        try:
            with open(path, "r", encoding="utf-8-sig") as f:
                tables[lang] = json.load(f)
        except Exception as e:
            print(f"ERROR ServerText: Failed to read {path}: {e}")
            return False
    unexpected = {
        p.stem.split(".")[1] for p in SERVER_TEXT_DIR.glob("ServerText.*.json")
    } - EXPECTED_LANGUAGES
    if unexpected:
        print(f"MISMATCH ServerText - language file(s) for unsupported language(s): {sorted(unexpected)}")
        return False

    reference = tables["en"]
    ok = True
    for lang, table in sorted(tables.items()):
        if lang == "en":
            continue
        missing = set(reference) - set(table)
        extra = set(table) - set(reference)
        if missing or extra:
            ok = False
            print(f"MISMATCH ServerText {lang}:")
            if missing:
                print(f"    Missing: {sorted(missing)}")
            if extra:
                print(f"    Extra: {sorted(extra)}")
            continue
        for key, template in table.items():
            if not isinstance(template, str) or not template.strip():
                ok = False
                print(f"MISMATCH ServerText {lang}: empty or non-string value for '{key}'")
            elif sorted(PLACEHOLDER.findall(template)) != sorted(PLACEHOLDER.findall(reference[key])):
                ok = False
                print(f"MISMATCH ServerText {lang}: placeholders of '{key}' differ from en "
                      f"({sorted(PLACEHOLDER.findall(template))} vs {sorted(PLACEHOLDER.findall(reference[key]))})")
    if ok:
        print(f"OK ServerText - {len(tables)} files, {len(reference)} keys")
    return ok


def main():
    if not I18N_DIR.exists():
        print(f"ERROR: i18n directory not found: {I18N_DIR}")
        sys.exit(1)

    tables = discover_tables()
    if not tables:
        print("ERROR: No i18n tables found")
        sys.exit(1)

    all_consistent = True
    for table in tables:
        if not check_table(table):
            all_consistent = False

    if not check_server_texts():
        all_consistent = False

    sys.exit(0 if all_consistent else 1)


if __name__ == "__main__":
    main()
