#!/usr/bin/env python3
# Usage: python tools/check-i18n.py
# Verifies i18n file consistency: all language files for each table must have identical key sets.

import json
import sys
from pathlib import Path
from collections import defaultdict


EXPECTED_LANGUAGES = {"bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr",
                      "ga", "hr", "hu", "it", "lt", "lv", "mt", "nl", "pl", "pt",
                      "ro", "ru", "sk", "sl", "sv", "uk"}
I18N_DIR = Path("src/StudyLife.Client/i18ntext")


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

    sys.exit(0 if all_consistent else 1)


if __name__ == "__main__":
    main()
