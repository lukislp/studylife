# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only

"""Tests for tools/check-licences.py - run with `python3 -m unittest discover -s scripts/tests`.

Pure logic only (no dotnet, no NuGet cache): expression handling, the policy verdicts, nuspec
parsing and the notices rendering, against small policies built in the test.
"""

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "check_licences", Path(__file__).resolve().parents[2] / "tools" / "check-licences.py")
check = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(check)

DENIED = ["GPL-*", "AGPL-*", "LGPL-*"]


def make_policy(allowed=("MIT",), exceptions=()):
    return {
        "bundle": {"projects": ["src/StudyLife.Server/StudyLife.Server.csproj"]},
        "licenses": {"allowed": list(allowed), "denied": DENIED},
        "exception": list(exceptions),
        "component": [],
    }


def pkg(spdx, name="Pkg", **kw):
    return {"id": name, "version": "1.0.0", "license": spdx, "license_note": None,
            "license_file_text": None, "project_url": "", "holder": "Acme", "projects": {"src/StudyLife.Server/StudyLife.Server.csproj"}, **kw}


class Expressions(unittest.TestCase):
    def test_ids(self):
        self.assertEqual(
            check.license_ids("(MIT OR Apache-2.0) AND GPL-2.0-only WITH Classpath-exception-2.0"),
            ["MIT", "Apache-2.0", "GPL-2.0-only"])

    def test_denied_families(self):
        for spdx in ("GPL-3.0-only", "AGPL-3.0-or-later", "LGPL-2.1-only", "MIT OR GPL-2.0-only", "gpl-3.0"):
            self.assertTrue(check.is_denied(spdx, DENIED), spdx)
        for spdx in ("MIT", "MPL-2.0", "BSD-2-Clause"):
            self.assertFalse(check.is_denied(spdx, DENIED), spdx)

    def test_evaluate_or_and(self):
        def ok(i):
            return i == "MIT"
        self.assertTrue(check.evaluate("MIT", ok))
        self.assertTrue(check.evaluate("Apache-2.0 OR MIT", ok))
        self.assertFalse(check.evaluate("Apache-2.0 AND MIT", ok))
        self.assertTrue(check.evaluate("(Apache-2.0 OR MIT) AND MIT", ok))

    def test_bad_expression(self):
        for bad in ("MIT OR", "(MIT", "OR MIT", ""):
            with self.assertRaises(ValueError, msg=bad):
                check.evaluate(bad, lambda i: True)


class HolderNormalisation(unittest.TestCase):
    CASES = {
        "© Microsoft Corporation. All rights reserved.": "Microsoft Corporation",
        "Copyright © 2019-2026 J.Sakamoto, Mozilla Public License 2.0": "J.Sakamoto",
        "Copyright (c) 2007 James Newton-King": "James Newton-King",
        "(C) 2014-16 Foo Bar Inc.": "Foo Bar Inc",
        "Copyright 2020, 2021 The DM Mono Project Authors": "The DM Mono Project Authors",
        "Copyright (c) .NET Foundation and Contributors": ".NET Foundation and Contributors",
        "  Alexandre Mutel  ": "Alexandre Mutel",
        "Copyright © Microsoft Corporation 2015-2026. ALL RIGHTS RESERVED.": "Microsoft Corporation",
        "": "",
        "Copyright (c) 2020": "",
    }

    def test_cases(self):
        for raw, expected in self.CASES.items():
            self.assertEqual(check.normalize_holder(raw), expected, raw)


class Verdicts(unittest.TestCase):
    def test_allowed(self):
        self.assertEqual(check.classify(pkg("MIT"), make_policy())[0], "ok")

    def test_unknown_always_fails(self):
        self.assertEqual(check.classify(pkg(check.UNKNOWN), make_policy())[0], "unknown")

    def test_denied_beats_allowed(self):
        self.assertEqual(check.classify(pkg("GPL-3.0-only"), make_policy(allowed=("GPL-3.0-only",)))[0], "denied")

    def test_policy_rejects_a_denied_exception(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "p.toml"
            f.write_text('[bundle]\n[licenses]\nallowed=[]\ndenied=["GPL-*"]\n'
                         '[[exception]]\npackage="Pkg"\nlicense="GPL-3.0-only"\nreason="r"\nsource="s"\n')
            with self.assertRaises(ValueError):
                check.load_policy(f)

    def test_exception_matches_package_and_licence_exactly(self):
        exc = {"package": "Toolbelt", "license": "MPL-2.0", "reason": "file-level copyleft", "source": "s"}
        policy = make_policy(exceptions=[exc])
        self.assertEqual(check.classify(pkg("MPL-2.0", "toolbelt"), policy)[0], "exception")
        self.assertEqual(check.classify(pkg("MPL-2.0", "Other"), policy)[0], "not-allowed")
        self.assertEqual(check.classify(pkg("MPL-1.1", "Toolbelt"), policy)[0], "not-allowed")

    def test_inventory_problems(self):
        with tempfile.TemporaryDirectory() as d:
            texts = Path(d)
            (texts / "MIT.txt").write_text("text")
            inv = [pkg("MIT", "A"), pkg(check.UNKNOWN, "B", license_note="only a licenseUrl"),
                   pkg("GPL-2.0-only", "C"), pkg("ISC", "D")]
            problems = check.check_inventory(inv, make_policy(), texts)
        self.assertEqual(len(problems), 3)
        self.assertTrue(any("B 1.0.0" in p and "could not be verified" in p for p in problems))
        self.assertTrue(any("C 1.0.0" in p and "denied" in p for p in problems))
        self.assertTrue(any("D 1.0.0" in p and "not in the allowed" in p for p in problems))

    def test_missing_licence_text(self):
        with tempfile.TemporaryDirectory() as d:
            problems = check.check_inventory([pkg("MIT")], make_policy(), Path(d))
        self.assertEqual(len(problems), 1)
        self.assertIn("texts/MIT.txt", problems[0])


class NuspecAndAssets(unittest.TestCase):
    NS = 'xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"'

    def nuspec(self, body):
        return check.parse_nuspec(f"<package {self.NS}><metadata>{body}</metadata></package>")

    def test_expression(self):
        meta = self.nuspec('<license type="expression">MIT</license><projectUrl>u</projectUrl>'
                           "<copyright>c</copyright>")
        self.assertEqual(check.resolve_license(meta, Path("."))[0], "MIT")
        self.assertEqual((meta["projectUrl"], meta["copyright"]), ("u", "c"))

    def test_license_url_only_is_unknown(self):
        meta = self.nuspec("<licenseUrl>https://example.invalid/LICENSE</licenseUrl>")
        spdx, note, _ = check.resolve_license(meta, Path("."))
        self.assertEqual(spdx, check.UNKNOWN)
        self.assertIn("licenseUrl", note)

    def test_nothing_is_unknown(self):
        self.assertEqual(check.resolve_license(self.nuspec("<id>x</id>"), Path("."))[0], check.UNKNOWN)

    def test_license_file(self):
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "LICENSE.txt").write_text("custom terms\n")
            meta = self.nuspec('<license type="file">LICENSE.txt</license>')
            spdx, _, text = check.resolve_license(meta, Path(d))
            self.assertEqual((spdx, text), (check.FILE_LICENSE, "custom terms"))
            missing = self.nuspec('<license type="file">NOPE.txt</license>')
            self.assertEqual(check.resolve_license(missing, Path(d))[0], check.UNKNOWN)

    def test_inventory_from_assets(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            pdir = root / "cache" / "foo" / "1.2.3"
            pdir.mkdir(parents=True)
            (pdir / "foo.nuspec").write_text(
                f'<package {self.NS}><metadata><license type="expression">MIT</license></metadata></package>')
            assets = root / "a.json"
            assets.write_text(json.dumps({
                "packageFolders": {str(root / "cache"): {}},
                "libraries": {"Foo/1.2.3": {"type": "package", "path": "foo/1.2.3"},
                              "Proj/1.0.0": {"type": "project", "path": "x"}}}))
            inv = check.build_inventory({"a": assets, "b": assets})
        self.assertEqual([(p["id"], p["version"], p["license"]) for p in inv], [("Foo", "1.2.3", "MIT")])
        self.assertEqual(inv[0]["projects"], {"a", "b"})


def notices_for(root, packages, tag, holders=None):
    """Fake NuGet cache + assets file for {id: (version, licence expression or None)}; returns
    (inventory, problems, rendered notices) the way main() computes them."""
    libs = {}
    for name, spec in packages.items():
        version, expr = spec[0], spec[1]
        cr = spec[2] if len(spec) > 2 else f"Copyright (c) 2020 {name} Authors"
        pdir = root / tag / "cache" / name.lower() / version
        pdir.mkdir(parents=True, exist_ok=True)
        lic = f'<license type="expression">{expr}</license>' if expr else ""
        if cr:
            lic += f"<copyright>{cr}</copyright>"
        (pdir / f"{name.lower()}.nuspec").write_text(
            f'<package {NuspecAndAssets.NS}><metadata><projectUrl>https://x/{name}</projectUrl>{lic}'
            "</metadata></package>")
        libs[f"{name}/{version}"] = {"type": "package", "path": f"{name.lower()}/{version}"}
    assets = root / tag / "a.json"
    assets.write_text(json.dumps({"packageFolders": {str(root / tag / "cache"): {}}, "libraries": libs}))
    texts = root / "texts"
    texts.mkdir(exist_ok=True)
    for lid in ("MIT", "BSD-2-Clause"):
        (texts / f"{lid}.txt").write_text(f"{lid} text")
    policy = make_policy(allowed=("MIT", "BSD-2-Clause"))
    inv = check.build_inventory({"src/StudyLife.Server/StudyLife.Server.csproj": assets}, holders)
    return inv, check.check_inventory(inv, policy, texts), check.render_notices(inv, policy, texts)


class VersionIndependentNotices(unittest.TestCase):
    BASE = {"Markdig": ("1.6.0", "BSD-2-Clause"), "Microsoft.Foo": ("10.0.14", "MIT")}

    def render(self, packages, holders=None):
        with tempfile.TemporaryDirectory() as d:
            return notices_for(Path(d), packages, "p", holders)

    def test_version_bump_with_same_licence_is_not_stale(self):
        _, problems, before = self.render(self.BASE)
        bumped = {**self.BASE, "Markdig": ("1.7.2", "BSD-2-Clause"), "Microsoft.Foo": ("10.0.20", "MIT")}
        _, problems_after, after = self.render(bumped)
        self.assertEqual((problems, problems_after), ([], []))
        self.assertEqual(before, after)
        self.assertNotIn("1.6.0", before)

    def test_licence_change_makes_the_notices_stale(self):
        _, _, before = self.render(self.BASE)
        _, _, after = self.render({**self.BASE, "Markdig": ("1.7.0", "MIT")})
        self.assertNotEqual(before, after)

    def test_added_and_removed_package_make_the_notices_stale(self):
        _, _, before = self.render(self.BASE)
        _, _, added = self.render({**self.BASE, "New.Pkg": ("1.0.0", "MIT")})
        _, _, removed = self.render({"Markdig": self.BASE["Markdig"]})
        self.assertNotEqual(before, added)
        self.assertNotEqual(before, removed)

    def test_project_url_change_makes_the_notices_stale(self):
        a = [dict(pkg("MIT", "A"), project_url="https://one")]
        b = [dict(pkg("MIT", "A"), project_url="https://two")]
        with tempfile.TemporaryDirectory() as d:
            texts = Path(d)
            (texts / "MIT.txt").write_text("MIT text")
            self.assertNotEqual(check.render_notices(a, make_policy(), texts),
                                check.render_notices(b, make_policy(), texts))

    def test_copyright_year_change_is_not_stale(self):
        a = {"Markdig": ("1.6.0", "MIT", "Copyright (c) 2019-2025 Alexandre Mutel. All rights reserved.")}
        b = {"Markdig": ("1.7.0", "MIT", "(C) 2019-2026 Alexandre Mutel")}
        self.assertEqual(self.render(a)[2], self.render(b)[2])
        self.assertIn("Copyright (c) Alexandre Mutel", self.render(a)[2])

    def test_holder_change_makes_the_notices_stale(self):
        a = {"Markdig": ("1.6.0", "MIT", "Copyright (c) 2020 Alexandre Mutel")}
        b = {"Markdig": ("1.7.0", "MIT", "Copyright (c) 2020 Someone Else")}
        self.assertNotEqual(self.render(a)[2], self.render(b)[2])

    def test_notice_lists_holder_with_its_packages_once_per_licence(self):
        text = self.render({"A": ("1", "MIT", "(c) 2020 Acme"), "B": ("1", "MIT", "Copyright 2021 Acme"),
                            "C": ("1", "MIT", "(c) 2020 Other Co")})[2]
        self.assertEqual(text.count("Copyright (c) Acme\n  Packages: A, B"), 1)
        self.assertIn("Copyright (c) Other Co\n  Packages: C", text)
        self.assertEqual(text.count("MIT text"), 1)

    def test_missing_holder_fails_unless_declared_in_policy(self):
        pkgs = {"Anon": ("1.0.0", "MIT", "")}
        inv, problems, _ = self.render(pkgs)
        self.assertEqual(inv[0]["holder"], "")
        self.assertEqual(len(problems), 1)
        self.assertIn("copyright holder", problems[0])
        inv, problems, text = self.render(pkgs, {"anon": "Anon Authors"})
        self.assertEqual(problems, [])
        self.assertIn("Copyright (c) Anon Authors", text)

    def test_authors_are_the_fallback_for_a_missing_copyright(self):
        self.assertEqual(check.copyright_holder({"authors": "J.Sakamoto"}), "J.Sakamoto")
        self.assertEqual(check.copyright_holder({"owners": "Jane Doe"}), "Jane Doe")
        self.assertEqual(check.copyright_holder({"copyright": "(c) 2020", "authors": "Jane"}), "Jane")
        self.assertEqual(check.copyright_holder({}), "")

    def test_unreadable_licence_fails_even_when_only_the_version_changed(self):
        _, problems, _ = self.render({**self.BASE, "Markdig": ("1.7.0", None)})
        self.assertEqual(len(problems), 1)
        self.assertIn("could not be verified", problems[0])

    def test_denied_licence_in_a_new_version_fails(self):
        _, problems, _ = self.render({**self.BASE, "Markdig": ("2.0.0", "GPL-3.0-only")})
        self.assertTrue(any("denied" in p for p in problems))


class Rendering(unittest.TestCase):
    def test_notices_are_deterministic_and_complete(self):
        exc = {"package": "Tool", "license": "MPL-2.0", "reason": "file-level copyleft", "source": "https://src"}
        policy = make_policy(exceptions=[exc])
        with tempfile.TemporaryDirectory() as d:
            texts = Path(d)
            (texts / "MIT.txt").write_text("MIT TEXT")
            inv = [pkg("MPL-2.0", "Tool"), pkg("MIT", "A", copyright="(c) A")]
            out = check.render_notices(inv, policy, texts)
            self.assertEqual(out, check.render_notices(inv, policy, texts))
        self.assertIn("| A | MIT |", out)
        self.assertIn("MIT TEXT", out)
        self.assertIn("Source code of this component: https://src", out)

    def test_asset_declared(self):
        good = r'<Content Include="..\..\N.md" Link="wwwroot\N.md" />'
        self.assertEqual(check.check_asset_declared(good, "N.md"), [])
        self.assertEqual(check.check_asset_declared(good.replace("wwwroot", "elsewhere"), "N.md").__len__(), 1)
        self.assertEqual(len(check.check_asset_declared("<ItemGroup/>", "N.md")), 1)

    def test_not_covered_section_is_rendered_when_the_policy_has_one(self):
        policy = make_policy()
        policy["bundle"]["not_covered"] = "\nThe image has more.\n"
        with tempfile.TemporaryDirectory() as d:
            out = check.render_notices([], policy, Path(d))
        self.assertIn("## Not covered by this list\n\nThe image has more.", out)


class VerifiedLicences(unittest.TestCase):
    """[[verified]] fills in a licence only where the nuspec states none."""

    def inventory(self, nuspec_body, verified):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            pdir = root / "cache" / "foo" / "1.0.0"
            pdir.mkdir(parents=True)
            (pdir / "foo.nuspec").write_text(
                f'<package {NuspecAndAssets.NS}><metadata><copyright>Copyright 2020 Foo</copyright>'
                f"{nuspec_body}</metadata></package>")
            assets = root / "a.json"
            assets.write_text(json.dumps({"packageFolders": {str(root / "cache"): {}},
                                          "libraries": {"Foo/1.0.0": {"type": "package", "path": "foo/1.0.0"}}}))
            return check.build_inventory({"p": assets}, None, verified)

    def test_applies_to_a_licence_url_only_package(self):
        inv = self.inventory("<licenseUrl>https://x/LICENSE</licenseUrl>", {"foo": "MIT"})
        self.assertEqual(inv[0]["license"], "MIT")

    def test_never_overrides_a_declared_licence(self):
        inv = self.inventory('<license type="expression">Apache-2.0</license>', {"foo": "MIT"})
        self.assertEqual(inv[0]["license"], "Apache-2.0")

    def test_without_an_entry_the_package_stays_unknown(self):
        inv = self.inventory("<licenseUrl>https://x/LICENSE</licenseUrl>", {})
        self.assertEqual(inv[0]["license"], check.UNKNOWN)

    def test_a_denied_licence_cannot_be_verified_into_the_policy(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "p.toml"
            f.write_text('[bundle]\n[licenses]\nallowed=[]\ndenied=["GPL-*"]\n'
                         '[[verified]]\npackage="Pkg"\nlicense="GPL-3.0-only"\nreason="r"\n')
            with self.assertRaises(ValueError):
                check.load_policy(f)


class RealPolicy(unittest.TestCase):
    def test_committed_policy_loads_and_every_text_exists(self):
        policy = check.load_policy(check.HERE / "policy.toml")
        self.assertTrue(set(policy["bundle"]["projects"]))
        for project in policy["bundle"]["projects"]:
            self.assertTrue((check.ROOT / project).is_file(), project)
        self.assertEqual(policy["licenses"]["denied"], ["GPL-*", "AGPL-*", "LGPL-*"])
        for lid in policy["licenses"]["allowed"]:
            self.assertTrue((check.HERE / "texts" / f"{lid}.txt").is_file(), lid)
        for comp in policy["component"]:
            self.assertTrue((check.HERE / "texts" / f"{comp['text']}.txt").is_file(), comp["name"])

    def test_the_client_csproj_ships_the_committed_notices(self):
        policy = check.load_policy(check.HERE / "policy.toml")
        csproj = (check.ROOT / policy["bundle"]["ship_csproj"]).read_text(encoding="utf-8-sig")
        self.assertEqual(check.check_asset_declared(csproj, policy["bundle"]["notices"]), [])
        self.assertTrue((check.ROOT / policy["bundle"]["notices"]).is_file())


if __name__ == "__main__":
    unittest.main()
