#!/usr/bin/env python3
# Usage: python tools/coverage-report.py
# Runs the full test suite with coverlet coverage collection, merges the Cobertura files via
# reportgenerator (local dotnet tool, see .config/dotnet-tools.json) into an HTML report under
# coverage-report/ and prints a plain-text summary. Reporting tool only: always exits 0.

import shutil
import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
TESTS_DIR = REPO_ROOT / "tests"
REPORT_DIR = REPO_ROOT / "coverage-report"
# Coverage goal only applies to these assemblies; Client/BrowserDebugProxy etc. would dilute the numbers.
ASSEMBLY_FILTERS = "+StudyLife.Server;+StudyLife.Shared"
# EF Core migrations are auto-generated boilerplate (Up/Down column diffs), not business logic -
# excluding them keeps the 90% goal meaningful instead of being inflated by scaffolded code that
# runs once at startup and is never meant to be "tested" in the normal sense. The former Planner
# exclusions (StudyPlanner/ExamPlanRequestDto/PlanProposal/PlannerController) were removed once the
# Planner protection rule was lifted (2026-07-19) - those classes now have their own tests
# (StudyPlannerTests, PlannerControllerTests) and count toward the 90% goal.
CLASS_FILTERS = "-StudyLife.Server.Migrations.*"


def clean_stale_results():
    """Remove old TestResults dirs so the glob below only sees this run's coverage files."""
    for test_results in TESTS_DIR.glob("*/TestResults"):
        shutil.rmtree(test_results, ignore_errors=True)


def run_tests_with_coverage():
    cmd = ["dotnet", "test", "StudyLife.sln", "--configuration", "Release",
           "--collect:XPlat Code Coverage"]
    result = subprocess.run(cmd, cwd=REPO_ROOT)
    if result.returncode != 0:
        print(f"WARNING: dotnet test exited with code {result.returncode} - "
              "coverage below may be incomplete.")


def find_coverage_files():
    return sorted(TESTS_DIR.glob("*/TestResults/*/coverage.cobertura.xml"))


def generate_report(coverage_files):
    reports = ";".join(str(f) for f in coverage_files)
    cmd = ["dotnet", "tool", "run", "reportgenerator",
           f"-reports:{reports}",
           f"-targetdir:{REPORT_DIR}",
           "-reporttypes:Html;TextSummary",
           f"-assemblyfilters:{ASSEMBLY_FILTERS}",
           f"-classfilters:{CLASS_FILTERS}"]
    result = subprocess.run(cmd, cwd=REPO_ROOT)
    return result.returncode == 0


def print_summary():
    summary_file = REPORT_DIR / "Summary.txt"
    if not summary_file.exists():
        print("WARNING: reportgenerator produced no Summary.txt")
        return
    print(summary_file.read_text(encoding="utf-8-sig"))
    print(f"HTML report: {REPORT_DIR / 'index.html'}")


def main():
    clean_stale_results()
    run_tests_with_coverage()

    coverage_files = find_coverage_files()
    if not coverage_files:
        print("WARNING: no coverage.cobertura.xml files found - nothing to report.")
        sys.exit(0)
    print(f"Found {len(coverage_files)} coverage file(s):")
    for f in coverage_files:
        print(f"  - {f.relative_to(REPO_ROOT)}")

    if generate_report(coverage_files):
        print_summary()
    else:
        print("WARNING: reportgenerator failed - no report generated.")
    sys.exit(0)


if __name__ == "__main__":
    main()
