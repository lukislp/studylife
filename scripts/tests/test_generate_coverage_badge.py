"""Tests for scripts/generate_coverage_badge.py (run: python3 -m unittest discover -s scripts/tests)."""
import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock

_SCRIPT = pathlib.Path(__file__).resolve().parent.parent / "generate_coverage_badge.py"
_spec = importlib.util.spec_from_file_location("generate_coverage_badge", _SCRIPT)
badge = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(badge)


class BadgeMessageTests(unittest.TestCase):
    def test_shows_line_and_branch_with_one_decimal(self):
        result = badge.badge_for({"linecoverage": 92.7, "branchcoverage": 78.6})
        self.assertEqual(result["message"], "line 92.7% | branch 78.6%")
        self.assertEqual(result["label"], "coverage")
        self.assertEqual(result["schemaVersion"], 1)

    def test_truncates_instead_of_rounding_up(self):
        # 79.96 must not show as 80.0: the badge may never overstate the measurement.
        result = badge.badge_for({"linecoverage": 92.99, "branchcoverage": 79.96})
        self.assertEqual(result["message"], "line 92.9% | branch 79.9%")

    def test_exact_values_are_not_truncated_by_float_error(self):
        self.assertEqual(badge.floor1(80.0), "80.0")
        self.assertEqual(badge.floor1(82.9), "82.9")

    def test_without_branch_data_only_line_is_shown(self):
        result = badge.badge_for({"linecoverage": 50.0})
        self.assertEqual(result["message"], "line 50.0%")
        self.assertEqual(result["color"], "yellow")

    def test_colour_follows_the_lower_number(self):
        result = badge.badge_for({"linecoverage": 92.7, "branchcoverage": 78.6})
        self.assertEqual(result["color"], "green")
        result = badge.badge_for({"linecoverage": 93.3, "branchcoverage": 82.9})
        self.assertEqual(result["color"], "brightgreen")


class MainTests(unittest.TestCase):
    def _run(self, summary, *extra):
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(tmp, "Summary.json")
            out = os.path.join(tmp, "badge.json")
            with open(src, "w", encoding="utf-8") as f:
                json.dump({"summary": summary}, f)
            with mock.patch.dict(os.environ, {}, clear=False):
                os.environ.pop("GITHUB_STEP_SUMMARY", None)
                code = badge.main([src, out, *extra])
            with open(out, encoding="utf-8") as f:
                return code, json.load(f)

    def test_writes_the_badge_file(self):
        code, written = self._run({"linecoverage": 92.7, "branchcoverage": 78.6})
        self.assertEqual(code, 0)
        self.assertEqual(written["message"], "line 92.7% | branch 78.6%")

    def test_gate_fails_below_minimum_but_still_writes_the_badge(self):
        code, written = self._run({"linecoverage": 92.7, "branchcoverage": 78.6}, "--min-branch", "80")
        self.assertEqual(code, 1)
        self.assertIn("branch 78.6%", written["message"])

    def test_gate_passes_at_or_above_minimum(self):
        code, _ = self._run({"linecoverage": 93.3, "branchcoverage": 82.9}, "--min-line", "92", "--min-branch", "80")
        self.assertEqual(code, 0)

    def test_missing_branch_data_fails_a_branch_gate(self):
        code, _ = self._run({"linecoverage": 93.3}, "--min-branch", "80")
        self.assertEqual(code, 1)


if __name__ == "__main__":
    unittest.main()
