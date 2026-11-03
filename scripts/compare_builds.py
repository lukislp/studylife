#!/usr/bin/env python3
"""Compare two build output directories file by file (SHA-256).

Usage: compare_builds.py <dir-a> <dir-b> [--summary <file>] [--known <glob>]...

Used by .github/workflows/reproducibility.yml to show that two clean
publishes of the same commit produce byte-identical files (docs/REPRODUCIBLE-BUILDS.md). Exit code 0 = identical,
1 = at least one file differs or exists on one side only. With --summary the
result table is also appended to that file (GitHub's $GITHUB_STEP_SUMMARY).

--known <glob> (repeatable, fnmatch on the relative path) marks a difference as
a known, documented exception: it is still listed, but does not fail the run.
Anything else that differs fails it, so a new source of non-determinism is
caught the first time it appears.
"""
import fnmatch
import hashlib
import sys
from pathlib import Path


def digests(root: Path) -> dict[str, str]:
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_file():
            result[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def compare(a: Path, b: Path) -> tuple[int, list[str]]:
    da, db = digests(a), digests(b)
    problems = []
    for name in sorted(da.keys() | db.keys()):
        if name not in db:
            problems.append(f"only in A: {name}")
        elif name not in da:
            problems.append(f"only in B: {name}")
        elif da[name] != db[name]:
            problems.append(f"differs:   {name}")
    return len(da.keys() | db.keys()), problems


def main(argv: list[str]) -> int:
    args = list(argv)
    summary = None
    known: list[str] = []
    while "--known" in args:
        i = args.index("--known")
        known.append(args[i + 1])
        del args[i : i + 2]
    if "--summary" in args:
        i = args.index("--summary")
        summary = Path(args[i + 1])
        del args[i : i + 2]
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    a, b = Path(args[0]), Path(args[1])
    total, problems = compare(a, b)
    def is_known(problem: str) -> bool:
        name = problem.split(": ", 1)[1].strip()
        return any(fnmatch.fnmatch(name, pattern) for pattern in known)
    expected = [p for p in problems if is_known(p)]
    unexpected = [p for p in problems if not is_known(p)]
    lines = [
        f"Compared {total} files: {total - len(problems)} identical, "
        f"{len(expected)} known exceptions, {len(unexpected)} unexpected differences."
    ]
    lines += [f"[known] {p}" for p in expected] + [f"[NEW]   {p}" for p in unexpected]
    print("\n".join(lines))
    if summary is not None:
        with summary.open("a", encoding="utf-8") as f:
            f.write("### Reproducible build check\n\n" + lines[0] + "\n\n")
            if problems:
                f.write("```\n" + "\n".join(lines[1:]) + "\n```\n")
    return 1 if unexpected else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
