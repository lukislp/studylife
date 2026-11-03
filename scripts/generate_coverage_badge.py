#!/usr/bin/env python3
"""Turn a reportgenerator JsonSummary into a shields.io "endpoint" badge JSON.

Usage: generate_coverage_badge.py <summary.json> <out.json>
           [--min-line PCT] [--min-branch PCT]

Reads the top-level summary.linecoverage percentage (already scoped to the
StudyLife.Server/StudyLife.Shared assemblies via the caller's -assemblyfilters -
StudyLife.Client is deliberately excluded, since it's Blazor UI markup with no
unit-test practice here, not a lack of testing on the projects that ARE covered)
and writes it in the schema shields.io/endpoint expects, so the README badge
can point at the raw GitHub URL of the committed file with no external service.

Also reports branch coverage (summary.branchcoverage): printed, and appended to
$GITHUB_STEP_SUMMARY when that is set, so every CI run shows both numbers.
--min-line / --min-branch turn a drop below the given percentage into a
non-zero exit code (a failing CI step); without them the script only reports.
"""
import json
import os
import sys

THRESHOLDS = [(80, "brightgreen"), (60, "green"), (40, "yellow"), (20, "orange")]


def color_for(pct: float) -> str:
    for threshold, color in THRESHOLDS:
        if pct >= threshold:
            return color
    return "red"


def parse_args(argv: list[str]) -> tuple[str, str, float | None, float | None]:
    args = list(argv)
    minimums: dict[str, float | None] = {"--min-line": None, "--min-branch": None}
    for flag in minimums:
        if flag in args:
            i = args.index(flag)
            minimums[flag] = float(args[i + 1])
            del args[i : i + 2]
    if len(args) != 2:
        raise SystemExit(__doc__)
    return args[0], args[1], minimums["--min-line"], minimums["--min-branch"]


def main(argv: list[str]) -> int:
    summary_path, out_path, min_line, min_branch = parse_args(argv)
    with open(summary_path, encoding="utf-8") as f:
        summary = json.load(f)["summary"]
    pct = summary["linecoverage"]
    branch = summary.get("branchcoverage")

    badge = {
        "schemaVersion": 1,
        "label": "coverage",
        "message": f"{pct:.0f}%",
        "color": color_for(pct),
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(badge, f)

    branch_text = "n/a" if branch is None else f"{branch:.1f}%"
    report = (
        f"Line coverage: {pct:.1f}% "
        f"({summary.get('coveredlines', '?')}/{summary.get('coverablelines', '?')} lines)\n"
        f"Branch coverage: {branch_text} "
        f"({summary.get('coveredbranches', '?')}/{summary.get('totalbranches', '?')} branches)"
    )
    print(report)
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as f:
            f.write("### Coverage (all assemblies except StudyLife.Client, migrations excluded)\n\n")
            f.write("| Metric | Value |\n|---|---|\n")
            f.write(f"| Line | {pct:.1f}% |\n| Branch | {branch_text} |\n")

    failed = False
    if min_line is not None and pct < min_line:
        print(f"Line coverage {pct:.1f}% is below the minimum of {min_line:.1f}%", file=sys.stderr)
        failed = True
    if min_branch is not None and (branch is None or branch < min_branch):
        print(f"Branch coverage {branch_text} is below the minimum of {min_branch:.1f}%", file=sys.stderr)
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
