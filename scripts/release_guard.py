# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only

"""Release guards for the CI/CD release chain (.github/workflows/ci-cd.yml).

Two checks, both of which exist because v3.24.0 went out unsigned (2026-11-04):

``drift``
    Formerly run by the release job before it created a release; the lktec-bot release module
    now skips a release whose commit is no longer the tip of main. server.zip is built by
    publish-server from the commit of the run, and the release has to be cut from that same
    commit. If a releasable commit landed in between, the
    release would carry a server.zip built from an older tree under a newer version (that is
    what happened to v3.24.0: built as 3.23.49 from 1a877c7f, released as 3.24.0 at 8c3a5405,
    and the signing step then found no v3.23.49 release and stopped). The release may only be
    cut when every commit between the two is a ``[skip ci]`` deploy bump that touches nothing
    but the deploy manifests and the coverage badge. Anything else fails the job before a
    release exists; the newer commit has its own workflow run, which releases it.

``assets``
    Run after the release. Every one of the latest releases (OpenSSF Scorecard looks at the
    last five) must carry server.zip together with its Sigstore bundle and its build
    provenance, so a missing signature turns the run red instead of only showing up as a lower
    Scorecard weeks later.

Usage::

    python3 scripts/release_guard.py drift --built <sha> [--head HEAD]
    gh api 'repos/<owner>/<repo>/releases?per_page=5' > releases.json
    python3 scripts/release_guard.py assets --releases-json releases.json [--window 5]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections.abc import Iterable, Sequence

# Paths the deploy-bump job (and nothing that affects server.zip) writes to main.
DRIFT_ALLOWED_PREFIXES = ("k8s/", ".github/badges/")
SKIP_CI_MARKER = "[skip ci]"

ASSET = "server.zip"
REQUIRED_ASSETS = (ASSET, f"{ASSET}.sigstore.json", f"{ASSET}.intoto.jsonl")

# Releases published without the signature files, with the reason. They are reported, not
# failed: assets of a published release are never replaced, so they can only age out of the
# window. Add an entry only together with an explanation in SECURITY.md.
KNOWN_UNSIGNED = {
    "v3.24.0": (
        "server.zip was built as 3.23.49 from 1a877c7f; a release race cut the tag at 8c3a5405 "
        "and the signing step stopped (run 41784615467). See SECURITY.md."
    ),
}


def drift_problems(built: str, head: str, commits: Sequence[tuple[str, str]], files: Iterable[str]) -> list[str]:
    """Reasons why a release at ``head`` would not match a server.zip built at ``built``.

    ``commits`` are (sha, full message) pairs for ``built..head``; ``files`` are the paths that
    differ between the two trees. An empty result means releasing at ``head`` is safe.
    """
    if built == head:
        return []
    problems = []
    for sha, message in commits:
        if SKIP_CI_MARKER not in message:
            subject = message.splitlines()[0] if message else ""
            problems.append(f"commit {sha[:12]} ({subject}) landed after the build and is not a [skip ci] deploy bump")
    for path in files:
        if not path.startswith(DRIFT_ALLOWED_PREFIXES):
            problems.append(f"{path} changed after the build")
    return problems


def asset_problems(releases: Sequence[dict], window: int, known_unsigned: dict[str, str] | None = None) -> tuple[list[str], list[str]]:
    """Check the newest ``window`` published releases for the three release files.

    Returns (problems, notices): problems fail the check, notices are known exceptions.
    """
    known = KNOWN_UNSIGNED if known_unsigned is None else known_unsigned
    published = [r for r in releases if not r.get("draft")]
    published.sort(key=lambda r: r.get("created_at") or "", reverse=True)
    problems, notices = [], []
    for release in published[:window]:
        tag = release.get("tag_name", "?")
        names = {a.get("name") for a in release.get("assets") or []}
        missing = [name for name in REQUIRED_ASSETS if name not in names]
        if not missing:
            continue
        message = f"{tag} is missing {', '.join(missing)}"
        if tag in known:
            notices.append(f"{message} (known: {known[tag]})")
        else:
            problems.append(message)
    return problems, notices


def _git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def _drift(args: argparse.Namespace) -> int:
    built = _git("rev-parse", "--verify", f"{args.built}^{{commit}}").strip()
    head = _git("rev-parse", "--verify", f"{args.head}^{{commit}}").strip()
    if subprocess.run(["git", "merge-base", "--is-ancestor", built, head]).returncode != 0:
        print(f"::error::the built commit {built} is not an ancestor of {head}; refusing to release")
        return 1
    log = _git("log", "--format=%H%x00%B%x1e", f"{built}..{head}")
    commits = []
    for entry in log.split("\x1e"):
        entry = entry.strip("\n")
        if entry:
            sha, _, message = entry.partition("\x00")
            commits.append((sha, message.strip()))
    files = [line for line in _git("diff", "--name-only", built, head).splitlines() if line]
    problems = drift_problems(built, head, commits, files)
    if problems:
        for problem in problems:
            print(f"::error::{problem}")
        print(
            f"::error::server.zip was built from {built[:12]}, but main is at {head[:12]} with changes that "
            "belong in the release. Not releasing from this run; the workflow run of the newer commit releases it."
        )
        return 1
    print(f"release tree matches the build: {built[:12]} -> {head[:12]} ({len(commits)} deploy bump(s) in between)")
    return 0


def _assets(args: argparse.Namespace) -> int:
    with open(args.releases_json, encoding="utf-8") as handle:
        releases = json.load(handle)
    problems, notices = asset_problems(releases, args.window)
    for notice in notices:
        print(f"::notice::{notice}")
    for problem in problems:
        print(f"::error::{problem}")
    if problems:
        return 1
    print(f"the latest {args.window} releases carry {', '.join(REQUIRED_ASSETS)}" + (" (known exceptions above)" if notices else ""))
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    drift = sub.add_parser("drift", help="refuse a release whose tree differs from the built one")
    drift.add_argument("--built", required=True, help="commit server.zip was built from")
    drift.add_argument("--head", default="HEAD", help="commit the release would be cut from")
    drift.set_defaults(func=_drift)
    assets = sub.add_parser("assets", help="check the latest releases for the signature files")
    assets.add_argument("--releases-json", required=True, help="output of GET /repos/{owner}/{repo}/releases")
    assets.add_argument("--window", type=int, default=5, help="how many of the newest releases to check")
    assets.set_defaults(func=_assets)
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
