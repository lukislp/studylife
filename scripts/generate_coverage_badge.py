#!/usr/bin/env python3
"""Turn a reportgenerator JsonSummary into a shields.io "endpoint" badge JSON.

Usage: generate_coverage_badge.py <summary.json> <out.json>
Reads the top-level summary.linecoverage percentage (already scoped to the
StudyLife.Server/StudyLife.Shared assemblies via the caller's -assemblyfilters -
StudyLife.Client is deliberately excluded, since it's Blazor UI markup with no
unit-test practice here, not a lack of testing on the projects that ARE covered)
and writes it in the schema shields.io/endpoint expects, so the README badge
can point at the raw GitHub URL of the committed file with no external service.
"""
import json
import sys

THRESHOLDS = [(80, "brightgreen"), (60, "green"), (40, "yellow"), (20, "orange")]


def color_for(pct: float) -> str:
    for threshold, color in THRESHOLDS:
        if pct >= threshold:
            return color
    return "red"


def main() -> None:
    summary_path, out_path = sys.argv[1], sys.argv[2]
    with open(summary_path, encoding="utf-8") as f:
        pct = json.load(f)["summary"]["linecoverage"]

    badge = {
        "schemaVersion": 1,
        "label": "coverage",
        "message": f"{pct:.0f}%",
        "color": color_for(pct),
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(badge, f)


if __name__ == "__main__":
    main()
