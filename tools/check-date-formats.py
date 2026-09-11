#!/usr/bin/env python3
# Usage: python tools/check-date-formats.py
#
# Guards against a regression of the "every date renders US-English" bug: the client is built
# with InvariantGlobalization=true (no ICU payload, see StudyLife.Client.csproj), so every
# culture-dependent DateTime format string silently falls back to the invariant culture -
# "ddd" always produced "Thu", "d"/"g" always produced 08/30/2026, in all 26 languages.
#
# All date formatting in the client therefore has to go through
# src/StudyLife.Client/Services/LocalDate.cs, which combines a per-language numeric pattern
# with weekday/month names read from the browser's own Intl data. This script fails when a raw
# culture-dependent format reappears anywhere else under src/StudyLife.Client.

import re
import sys
from pathlib import Path

CLIENT_DIR = Path("src/StudyLife.Client")
SOURCE_SUFFIXES = (".cs", ".razor")

# LocalDate.cs is the one place that legitimately owns the patterns; its own doc comment also
# quotes the broken formats this script hunts for.
ALLOWED_FILES = {
    Path("src/StudyLife.Client/Services/LocalDate.cs"),
}

# Standard format specifiers whose output depends on the culture (short/long date, short/long
# date+time, month/year names), plus custom patterns containing a weekday or month NAME
# ("ddd"/"dddd"/"MMM"/"MMMM") or a hardwired day.month order.
CULTURE_FORMATS = {"d", "D", "f", "F", "g", "G", "m", "M", "t", "T", "y", "Y"}

PATTERNS = [
    # .ToString("<format>") / .ToString("<format>", <anything>) on a date-ish expression.
    re.compile(r'\.ToString\(\s*"(?P<fmt>[^"]*)"'),
    # Interpolation holes with a format: {expr:ddd} / {expr:MMM d, yyyy}
    re.compile(r'\{[^{}"\r\n]+:(?P<fmt>[^{}"\r\n]*[a-zA-Z][^{}"\r\n]*)\}'),
]

NAME_PATTERN = re.compile(r"ddd|MMM")
# ISO 8601, i.e. the machine-readable value format of <input type="date"/"datetime-local">
# and of date query parameters - a localized order would be a bug there, not a fix, so these
# are exempt even though they contain "MM-dd".
ISO_PATTERN = re.compile(r"^yyyy-MM-dd")
# A hardwired day/month display order such as "dd.MM.yyyy", "dd.MM." or "MM/dd/yyyy".
NUMERIC_DATE_PATTERN = re.compile(r"dd\s*[./-]\s*MM|MM\s*[./-]\s*dd")


def is_offending(fmt: str) -> bool:
    if fmt in CULTURE_FORMATS:
        return True
    if ISO_PATTERN.match(fmt):
        return False
    return bool(NAME_PATTERN.search(fmt) or NUMERIC_DATE_PATTERN.search(fmt))


def scan(path: Path):
    findings = []
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    for lineno, line in enumerate(text.splitlines(), start=1):
        for pattern in PATTERNS:
            for match in pattern.finditer(line):
                fmt = match.group("fmt")
                if is_offending(fmt):
                    findings.append((lineno, fmt, line.strip()))
    return findings


def main():
    if not CLIENT_DIR.exists():
        print(f"ERROR: client directory not found: {CLIENT_DIR} (run from the repository root)")
        sys.exit(1)

    offenders = []
    for path in sorted(CLIENT_DIR.rglob("*")):
        if path.suffix not in SOURCE_SUFFIXES or not path.is_file():
            continue
        if Path(*path.parts) in ALLOWED_FILES:
            continue
        for lineno, fmt, line in scan(path):
            offenders.append((path, lineno, fmt, line))

    if not offenders:
        print("OK: no raw culture-dependent date formats in src/StudyLife.Client")
        sys.exit(0)

    print("ERROR: culture-dependent date formats found - the client runs with")
    print("       InvariantGlobalization, so these render English/US in all 26 languages.")
    print("       Use StudyLife.Client.Services.LocalDate instead (Short/ShortYear/DayMonth/")
    print("       Time/DateAndTime/Weekday/MonthName).")
    print()
    for path, lineno, fmt, line in offenders:
        print(f"  {path.as_posix()}:{lineno}: format \"{fmt}\"")
        print(f"      {line}")
    sys.exit(1)


if __name__ == "__main__":
    main()
