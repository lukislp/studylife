#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only

"""Licence inventory, policy gate and THIRD-PARTY-NOTICES.md for what the server ships and runs.

    python3 tools/check-licences.py            # restore, check the policy, verify the notices file
    python3 tools/check-licences.py --update   # same, then rewrite THIRD-PARTY-NOTICES.md
    python3 tools/check-licences.py --assets obj/a.json obj/b.json   # skip the restore

The package set comes from the build system: `dotnet restore` of the server and of the browser
client writes obj/project.assets.json, which lists the whole resolved NuGet graph of that project.
Each package's licence is read from its .nuspec in the NuGet cache (an SPDX expression, or a licence
file shipped in the package). The rules live in tools/licences/policy.toml - this script holds no
package names.

The same approach (and most of the code) as the studylife-app repository's
scripts/licences/check.py, which does this for the native app bundle; the two are parallel copies
(see docs/ARCHITECTURE.md, "Third-party notices").

Exit status 1 on a policy violation, an unreadable licence, a missing licence text, a csproj that
does not ship the notices file, or a stale notices file. Tests: scripts/tests/test_check_licences.py.
"""

from __future__ import annotations

import argparse
import difflib
import fnmatch
import json
import re
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent / "licences"
ROOT = HERE.parent.parent
UNKNOWN = "UNKNOWN"
FILE_LICENSE = "LicenseRef-package-file"


# --- policy ----------------------------------------------------------------------------------


def load_policy(path: Path) -> dict:
    policy = tomllib.loads(path.read_text(encoding="utf-8"))
    for key in ("bundle", "licenses"):
        if key not in policy:
            raise ValueError(f"policy: missing [{key}]")
    policy.setdefault("exception", [])
    policy.setdefault("component", [])
    policy.setdefault("holder", [])
    policy.setdefault("verified", [])
    for v in policy["verified"]:
        for key in ("package", "license", "reason"):
            if not v.get(key):
                raise ValueError(f"policy: verified {v.get('package', '?')} needs '{key}'")
        if is_denied(v["license"], policy["licenses"]["denied"]):
            raise ValueError(f"policy: {v['package']}: a denied licence cannot be verified into the policy")
    for h in policy["holder"]:
        for key in ("package", "holder", "reason"):
            if not h.get(key):
                raise ValueError(f"policy: holder {h.get('package', '?')} needs '{key}'")
    for exc in policy["exception"]:
        for key in ("package", "license", "reason", "source"):
            if not exc.get(key):
                raise ValueError(f"policy: exception {exc.get('package', '?')} needs '{key}'")
        if is_denied(exc["license"], policy["licenses"]["denied"]):
            raise ValueError(f"policy: {exc['package']}: a denied licence cannot be an exception")
    return policy


def tokenize(expression: str) -> list[str]:
    return re.findall(r"\(|\)|[A-Za-z0-9.+:\-]+", expression)


def license_ids(expression: str) -> list[str]:
    """Every SPDX id in an expression (operators and WITH exceptions dropped)."""
    ids, tokens, i = [], tokenize(expression), 0
    while i < len(tokens):
        tok = tokens[i]
        if tok.upper() == "WITH":
            i += 2  # skip the exception id
            continue
        if tok not in ("(", ")") and tok.upper() not in ("AND", "OR"):
            ids.append(tok)
        i += 1
    return ids


def is_denied(expression: str, denied: list[str]) -> bool:
    return any(
        fnmatch.fnmatchcase(i.lower(), pat.lower()) for i in license_ids(expression) for pat in denied
    )


def evaluate(expression: str, ok) -> bool:
    """Evaluate an SPDX expression: OR = any alternative, AND = all, `ok(id)` judges one id."""
    tokens = tokenize(expression)
    pos = 0

    def peek():
        return tokens[pos] if pos < len(tokens) else None

    def take():
        nonlocal pos
        pos += 1
        return tokens[pos - 1]

    def parse_or():
        result = parse_and()
        while peek() and peek().upper() == "OR":
            take()
            right = parse_and()
            result = result or right
        return result

    def parse_and():
        result = parse_atom()
        while peek() and peek().upper() == "AND":
            take()
            right = parse_atom()
            result = result and right
        return result

    def parse_atom():
        tok = peek()
        if tok is None:
            raise ValueError(f"bad licence expression: {expression!r}")
        if tok == "(":
            take()
            result = parse_or()
            if peek() != ")":
                raise ValueError(f"bad licence expression: {expression!r}")
            take()
            return result
        if tok in (")",) or tok.upper() in ("AND", "OR", "WITH"):
            raise ValueError(f"bad licence expression: {expression!r}")
        take()
        if peek() and peek().upper() == "WITH":
            take()
            take()
        return ok(tok)

    result = parse_or()
    if pos != len(tokens):
        raise ValueError(f"bad licence expression: {expression!r}")
    return result


def classify(pkg: dict, policy: dict) -> tuple[str, str | None]:
    """Returns ("ok" | "exception" | "denied" | "unknown" | "not-allowed", detail)."""
    spdx = pkg["license"]
    lic = policy["licenses"]
    if spdx == UNKNOWN:
        return "unknown", pkg.get("license_note")
    if is_denied(spdx, lic["denied"]):
        return "denied", None
    allowed = {a.lower() for a in lic["allowed"]}
    if evaluate(spdx, lambda i: i.lower() in allowed):
        return "ok", None
    for exc in policy["exception"]:
        if exc["package"].lower() == pkg["id"].lower() and exc["license"] == spdx:
            return "exception", exc["reason"]
    return "not-allowed", None


# --- inventory from the build system ---------------------------------------------------------


def restore(csproj: Path) -> Path:
    subprocess.run(["dotnet", "restore", str(csproj), "--nologo", "-v", "q"], check=True)
    return csproj.parent / "obj" / "project.assets.json"


def label(project: str) -> str:
    """Short name of a project in the notices: src/StudyLife.Server/StudyLife.Server.csproj -> Server."""
    return Path(project).stem.removeprefix("StudyLife.")


def read_assets(path: Path) -> tuple[list[dict], list[Path]]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    folders = [Path(f) for f in data.get("packageFolders", {})]
    packages = []
    for key, lib in data.get("libraries", {}).items():
        if lib.get("type") != "package":
            continue
        name, _, version = key.partition("/")
        packages.append({"id": name, "version": version, "path": lib["path"]})
    return packages, folders


def find_package_dir(pkg: dict, folders: list[Path]) -> Path:
    for folder in folders:
        candidate = folder / pkg["path"]
        if candidate.is_dir():
            return candidate
    raise FileNotFoundError(f"{pkg['id']} {pkg['version']}: not in {[str(f) for f in folders]}")


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_nuspec(text: str) -> dict:
    meta = {}
    root = ET.fromstring(text)
    for md in root:
        if _local(md.tag) != "metadata":
            continue
        for el in md:
            name = _local(el.tag)
            if name == "license":
                meta["license_type"] = el.get("type")
                meta["license_value"] = (el.text or "").strip()
            elif name in ("licenseUrl", "projectUrl", "copyright", "authors", "owners"):
                meta[name] = (el.text or "").strip()
    return meta


_YEAR = r"(?:19|20)\d{2}"
_YEARS = re.compile(rf"\b{_YEAR}(?:\s*[-\u2013,]\s*(?:{_YEAR}|\d{{2}}))*\b")


def normalize_holder(text: str) -> str:
    """Copyright holder without the parts that change per release: the word "Copyright", (c)/\u00a9
    marks, years and year ranges, "All rights reserved" and a trailing ", <name> licence" clause."""
    t = text or ""
    t = re.sub(r"all\s+rights\s+reserved\.?", " ", t, flags=re.I)
    t = re.sub(r"\bcopyright\b|\(c\)|\u00a9", " ", t, flags=re.I)
    t = _YEARS.sub(" ", t)
    parts = [part for part in re.split(r"\s*,\s*", t) if not re.search(r"licen[cs]e", part, re.I)]
    t = ", ".join(parts)
    t = re.sub(r"\s+", " ", t)
    return t.lstrip(" \t,;:-").rstrip(" \t.,;:-")


def copyright_holder(meta: dict) -> str:
    """Normalised holder from <copyright>, else <authors>, else <owners>; "" when none gives one."""
    for field in ("copyright", "authors", "owners"):
        holder = normalize_holder(meta.get(field, ""))
        if holder:
            return holder
    return ""


def resolve_license(meta: dict, package_dir: Path) -> tuple[str, str | None, str | None]:
    """(spdx or UNKNOWN, note, embedded licence file text)."""
    ltype, value = meta.get("license_type"), meta.get("license_value", "")
    if ltype == "expression" and value:
        return value, None, None
    if ltype == "file" and value:
        file = package_dir / value
        if file.is_file():
            return FILE_LICENSE, value, file.read_text(encoding="utf-8", errors="replace").strip()
        return UNKNOWN, f"licence file {value} missing from the package", None
    if meta.get("licenseUrl"):
        return UNKNOWN, f"only a licenseUrl: {meta['licenseUrl']}", None
    return UNKNOWN, "no licence information in the nuspec", None


def build_inventory(assets: dict[str, Path], holders: dict[str, str] | None = None,
                    verified: dict[str, str] | None = None) -> list[dict]:
    """assets: project (path as written in the policy) -> project.assets.json. One entry per (package id, licence, holder).

    `holders` (lower-case package id -> holder) comes from the policy and only applies to a package
    whose nuspec yields no holder. `verified` (lower-case package id -> SPDX id) likewise only
    applies to a package whose nuspec states no licence at all (UNKNOWN): a hand-verified licence
    from the policy, never a replacement for a licence the package declares itself.

    The version is deliberately not part of an entry's identity: the licence is read from the
    nuspec of the version resolved on this run, but a version bump with the same licence yields
    the same entry (and so the same notices file). `version` only serves error messages.
    """
    merged: dict[tuple[str, str], dict] = {}
    for tfm, path in assets.items():
        packages, folders = read_assets(path)
        for pkg in packages:
            pdir = find_package_dir(pkg, folders)
            nuspec = next(pdir.glob("*.nuspec"), None)
            if nuspec is None:
                raise FileNotFoundError(f"{pkg['id']} {pkg['version']}: no .nuspec in {pdir}")
            meta = parse_nuspec(nuspec.read_text(encoding="utf-8"))
            spdx, note, file_text = resolve_license(meta, pdir)
            if spdx == UNKNOWN and (verified or {}).get(pkg["id"].lower()):
                spdx, note = verified[pkg["id"].lower()], None
            holder = copyright_holder(meta) or (holders or {}).get(pkg["id"].lower(), "")
            key = (pkg["id"].lower(), spdx, holder)
            if key in merged:
                merged[key]["projects"].add(tfm)
                continue
            merged[key] = {
                "id": pkg["id"],
                "version": pkg["version"],
                "license": spdx,
                "license_note": note,
                "license_file_text": file_text,
                "holder": holder,
                "project_url": meta.get("projectUrl", ""),
                "projects": {tfm},
            }
    return sorted(merged.values(), key=lambda p: (p["id"].lower(), p["license"], p["holder"]))


# --- checks ----------------------------------------------------------------------------------


def check_inventory(inventory: list[dict], policy: dict, texts_dir: Path) -> list[str]:
    problems = []
    for pkg in inventory:
        status, detail = classify(pkg, policy)
        label = f"{pkg['id']} {pkg['version']} ({pkg['license']})"
        if status == "unknown":
            problems.append(f"{label}: licence could not be verified - {detail}")
        elif status == "denied":
            problems.append(f"{label}: denied licence family")
        elif status == "not-allowed":
            problems.append(f"{label}: not in the allowed list and no exception in the policy")
        elif status == "ok":
            for lid in license_ids(pkg["license"]):
                if lid.lower() in {a.lower() for a in policy["licenses"]["allowed"]} and not (
                    texts_dir / f"{lid}.txt"
                ).is_file():
                    problems.append(f"{label}: no licence text texts/{lid}.txt")
    for pkg in inventory:
        if not pkg.get("holder"):
            problems.append(f"{pkg['id']} {pkg['version']}: no copyright holder in the nuspec "
                            "(copyright, authors, owners); declare one with a reason in a policy [[holder]]")
    for comp in policy["component"]:
        if is_denied(comp["license"], policy["licenses"]["denied"]):
            problems.append(f"component {comp['name']}: denied licence family")
        if not (texts_dir / f"{comp['text']}.txt").is_file():
            problems.append(f"component {comp['name']}: no licence text texts/{comp['text']}.txt")
    return problems


def check_asset_declared(csproj_text: str, file_name: str) -> list[str]:
    """The browser client's csproj must publish the notices file as wwwroot/<file_name>, from where
    the server serves it as a static asset (the About section fetches it from there)."""
    for m in re.finditer(r"<Content\b[^>]*>", csproj_text):
        tag = m.group(0)
        if re.search(rf'Include="[^"]*{re.escape(file_name)}"', tag) and re.search(
            rf'Link="wwwroot[\\/]{re.escape(file_name)}"', tag
        ):
            return []
    return [f'csproj does not ship the notices file (no <Content Include="...{file_name}" '
            f'Link="wwwroot\\{file_name}" />)']


# --- notices file ----------------------------------------------------------------------------


def holder_lines(packages: list[dict]) -> list[str]:
    """One "Copyright (c) <holder>" line per distinct holder, followed by the packages it covers."""
    by_holder: dict[str, list[str]] = {}
    for p in packages:
        by_holder.setdefault(p["holder"], []).append(p["id"])
    lines = []
    for holder in sorted(by_holder, key=str.lower):
        names = ", ".join(sorted(set(by_holder[holder]), key=str.lower))
        lines += [f"Copyright (c) {holder}", f"  Packages: {names}", ""]
    return lines[:-1]


def render_notices(inventory: list[dict], policy: dict, texts_dir: Path) -> str:
    short = {p: label(p) for p in policy["bundle"]["projects"]}
    out = [
        "# Third-party notices",
        "",
        "<!-- Generated by tools/check-licences.py --update from the NuGet graph and",
        "     tools/licences/policy.toml. Do not edit by hand; CI fails when this file is stale. -->",
        "",
        "This file lists the third-party software that the StudyLife server runs and sends to the",
        "browser (the web build and the Docker image built from this repository), including the",
        "tools the project is built with that appear in the NuGet graph.",
        "StudyLife itself (github.com/lukislp/studylife) is copyright Lukas Koerber under",
        "AGPL-3.0-only (see LICENSE). No third-party NuGet package in this list is under a GPL,",
        "AGPL or LGPL licence.",
        "",
        "The list names packages, licences and copyright holders, not versions: it stays valid across",
        "version updates and changes only when a package is added or removed or its licence, copyright",
        "holder (years are left out) or project URL changes.",
        "",
        "## NuGet packages",
        "",
        "| Package | Licence | Project | Used in |",
        "| --- | --- | --- | --- |",
    ]
    for p in inventory:
        used = ", ".join(sorted(short.get(f, f) for f in p["projects"]))
        url = p["project_url"] or "-"
        out.append(f"| {p['id']} | {p['license']} | {url} | {used} |")

    by_license: dict[str, list[dict]] = {}
    for p in inventory:
        status, _ = classify(p, policy)
        if status == "ok":
            for lid in license_ids(p["license"]):
                by_license.setdefault(lid, []).append(p)

    for lid in sorted(by_license):
        out += ["", f"## {lid}", ""]
        out += holder_lines(by_license[lid])
        out += ["", "```text", (texts_dir / f"{lid}.txt").read_text(encoding="utf-8").strip(), "```"]

    for exc in policy["exception"]:
        matches = [p for p in inventory if p["id"].lower() == exc["package"].lower()]
        if not matches:
            continue
        out += ["", f"## {exc['license']}: {exc['package']}", ""]
        out += holder_lines(matches)
        out += [
            "",
            exc["reason"],
            "",
            f"Source code of this component: {exc['source']}",
            f"Licence text: https://spdx.org/licenses/{exc['license']}.html",
        ]

    for p in inventory:
        if p["license_file_text"]:
            out += ["", f"## Licence file shipped with {p['id']}", "", "```text",
                    p["license_file_text"], "```"]

    out += ["", "## Other components", ""]
    for comp in policy["component"]:
        out += [f"### {comp['name']}", "", f"Licence: {comp['license']}", f"Source: {comp['url']}", ""]
        out += [f"{line}" for line in comp.get("copyright", [])]
        out += ["", "```text", (texts_dir / f"{comp['text']}.txt").read_text(encoding="utf-8").strip(), "```", ""]
    if policy["bundle"].get("not_covered"):
        out += ["## Not covered by this list", "", policy["bundle"]["not_covered"].strip(), ""]
    return "\n".join(out).rstrip() + "\n"


# --- command line ----------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--policy", type=Path, default=HERE / "policy.toml")
    ap.add_argument("--update", action="store_true", help="rewrite the notices file")
    ap.add_argument("--assets", nargs="+", type=Path, help="project.assets.json files instead of a restore; "
                    "paired with the policy's projects in order")
    args = ap.parse_args(argv)

    policy = load_policy(args.policy)
    projects = policy["bundle"]["projects"]
    shipping_csproj = ROOT / policy["bundle"]["ship_csproj"]
    notices_path = ROOT / policy["bundle"]["notices"]
    texts_dir = HERE / "texts"

    if args.assets:
        if len(args.assets) != len(projects):
            print(f"need {len(projects)} --assets files, one per project {projects}", file=sys.stderr)
            return 2
        assets = dict(zip(projects, args.assets))
    else:
        assets = {project: restore(ROOT / project) for project in projects}

    holders = {h["package"].lower(): h["holder"] for h in policy["holder"]}
    verified = {v["package"].lower(): v["license"] for v in policy["verified"]}
    inventory = build_inventory(assets, holders, verified)
    problems = check_inventory(inventory, policy, texts_dir)
    problems += check_asset_declared(shipping_csproj.read_text(encoding="utf-8-sig"), notices_path.name)
    if problems:
        print("Licence policy violations:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    notices = render_notices(inventory, policy, texts_dir)
    if args.update:
        notices_path.write_bytes(notices.encode("utf-8"))
        print(f"wrote {notices_path.relative_to(ROOT)} ({len(inventory)} packages)")
        return 0
    current = notices_path.read_bytes().decode("utf-8").replace("\r\n", "\n") if notices_path.exists() else ""
    if current != notices:
        print(f"{notices_path.name} is stale. Run: python3 tools/check-licences.py --update", file=sys.stderr)
        diff = difflib.unified_diff(current.splitlines(), notices.splitlines(), "committed", "generated", lineterm="", n=0)
        for line in list(diff)[:60]:
            print(line, file=sys.stderr)
        return 1
    print(f"{len(inventory)} packages, policy satisfied, {notices_path.name} is current")
    return 0


if __name__ == "__main__":
    sys.exit(main())
