# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only

"""Tests for scripts/release_guard.py (run: python3 -m unittest discover -s scripts/tests)."""
import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO

_SCRIPT = pathlib.Path(__file__).resolve().parent.parent / "release_guard.py"
_spec = importlib.util.spec_from_file_location("release_guard", _SCRIPT)
guard = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(guard)

ALL_ASSETS = [{"name": n} for n in ("server.zip", "server.zip.sigstore.json", "server.zip.intoto.jsonl")]


class DriftProblemTests(unittest.TestCase):
    def test_same_commit_is_fine(self):
        self.assertEqual(guard.drift_problems("abc", "abc", [], []), [])

    def test_deploy_bump_in_between_is_fine(self):
        commits = [("1a877c7f00", "chore(deploy): image 3.23.48 [skip ci]")]
        files = ["k8s/04-web.yaml", "k8s/05-worker.yaml", ".github/badges/coverage.json"]
        self.assertEqual(guard.drift_problems("59bbe7a1", "1a877c7f", commits, files), [])

    def test_the_v3_24_0_race_is_refused(self):
        # Built at 1a877c7f as 3.23.49, then #422 (a feat) landed before the real release.
        commits = [("8c3a540542", "feat(security): optional TLS with private-CA validation for the add-on hops (#422)")]
        files = ["src/StudyLife.Server/Program.cs", "k8s/04-web.yaml"]
        problems = guard.drift_problems("1a877c7f", "8c3a5405", commits, files)
        self.assertEqual(len(problems), 2)
        self.assertIn("8c3a540542", problems[0])
        self.assertIn("not a [skip ci]", problems[0])
        self.assertIn("src/StudyLife.Server/Program.cs", problems[1])

    def test_skip_ci_commit_that_touches_source_is_refused(self):
        commits = [("deadbeef00", "fix: something [skip ci]")]
        problems = guard.drift_problems("a", "b", commits, ["src/StudyLife.Client/App.razor"])
        self.assertEqual(problems, ["src/StudyLife.Client/App.razor changed after the build"])

    def test_skip_ci_marker_in_the_body_counts(self):
        commits = [("deadbeef00", "chore(deploy): image 3.25.0\n\n[skip ci]")]
        self.assertEqual(guard.drift_problems("a", "b", commits, ["k8s/04-web.yaml"]), [])


class DriftGitTests(unittest.TestCase):
    """End to end against a throwaway repository."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = self._tmp.name
        self._cwd = os.getcwd()
        os.chdir(self.repo)
        for args in (
            ["init", "-q", "-b", "main"],
            ["config", "user.email", "test@example.invalid"],
            ["config", "user.name", "test"],
            ["config", "commit.gpgsign", "false"],
        ):
            self._git(*args)
        self.built = self._commit("src/app.cs", "v1", "fix: first")

    def tearDown(self):
        os.chdir(self._cwd)
        self._tmp.cleanup()

    def _git(self, *args):
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()

    def _commit(self, path, content, message):
        full = pathlib.Path(self.repo, path)
        full.parent.mkdir(parents=True, exist_ok=True)
        full.write_text(content, encoding="utf-8")
        self._git("add", path)
        self._git("commit", "-q", "-m", message)
        return self._git("rev-parse", "HEAD")

    def _run(self, *argv):
        out = StringIO()
        with redirect_stdout(out):
            code = guard.main(list(argv))
        return code, out.getvalue()

    def test_passes_on_the_built_commit(self):
        code, _ = self._run("drift", "--built", self.built)
        self.assertEqual(code, 0)

    def test_passes_over_a_deploy_bump(self):
        self._commit("k8s/04-web.yaml", "image: x:1", "chore(deploy): image 1.0.0 [skip ci]")
        code, out = self._run("drift", "--built", self.built)
        self.assertEqual(code, 0, out)
        self.assertIn("1 deploy bump", out)

    def test_fails_over_a_feature_commit(self):
        self._commit("src/app.cs", "v2", "feat: new thing")
        code, out = self._run("drift", "--built", self.built)
        self.assertEqual(code, 1)
        self.assertIn("::error::", out)
        self.assertIn("feat: new thing", out)

    def test_fails_when_built_is_not_an_ancestor(self):
        self._git("checkout", "-q", "-b", "side")
        side = self._commit("src/other.cs", "x", "fix: side")
        self._git("checkout", "-q", "main")
        code, out = self._run("drift", "--built", side)
        self.assertEqual(code, 1)
        self.assertIn("not an ancestor", out)


class AssetProblemTests(unittest.TestCase):
    def _release(self, tag, created, assets, draft=False):
        return {"tag_name": tag, "created_at": created, "assets": assets, "draft": draft}

    def test_all_signed_is_fine(self):
        releases = [self._release(f"v1.0.{i}", f"2026-11-0{i}T00:00:00Z", ALL_ASSETS) for i in range(1, 6)]
        self.assertEqual(guard.asset_problems(releases, 5, {}), ([], []))

    def test_missing_signature_is_a_problem(self):
        releases = [self._release("v2.0.0", "2026-11-04T00:00:00Z", [{"name": "server.zip"}])]
        problems, notices = guard.asset_problems(releases, 5, {})
        self.assertEqual(problems, ["v2.0.0 is missing server.zip.sigstore.json, server.zip.intoto.jsonl"])
        self.assertEqual(notices, [])

    def test_known_exception_is_only_a_notice(self):
        releases = [self._release("v3.24.0", "2026-11-04T06:28:53Z", [{"name": "server.zip"}])]
        problems, notices = guard.asset_problems(releases, 5, {"v3.24.0": "race"})
        self.assertEqual(problems, [])
        self.assertEqual(len(notices), 1)
        self.assertIn("known: race", notices[0])

    def test_real_known_list_covers_v3_24_0(self):
        self.assertIn("v3.24.0", guard.KNOWN_UNSIGNED)

    def test_only_the_newest_window_counts_and_drafts_are_ignored(self):
        old_unsigned = self._release("v0.9.0", "2026-01-01T00:00:00Z", [])
        newer = [self._release(f"v1.0.{i}", f"2026-11-0{i}T00:00:00Z", ALL_ASSETS) for i in range(1, 6)]
        draft = self._release("untagged-1", "2026-12-01T00:00:00Z", [], draft=True)
        self.assertEqual(guard.asset_problems([old_unsigned, *newer, draft], 5, {}), ([], []))

    def test_cli_reads_the_api_json(self):
        releases = [self._release("v9.9.9", "2026-11-04T00:00:00Z", [{"name": "server.zip"}])]
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp, "releases.json")
            path.write_text(json.dumps(releases), encoding="utf-8")
            out = StringIO()
            with redirect_stdout(out):
                code = guard.main(["assets", "--releases-json", str(path)])
        self.assertEqual(code, 1)
        self.assertIn("::error::v9.9.9 is missing", out.getvalue())


if __name__ == "__main__":
    unittest.main()
