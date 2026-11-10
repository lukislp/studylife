# Contributing to StudyLife

Thanks for taking the time. StudyLife is a single-maintainer project, so the process is deliberately
small - but it is the same for every change, including the maintainer's own.

## How changes get in

1. Open an issue first for anything bigger than a typo or an obvious bug fix, so the direction can
   be agreed before you spend time on it. Use the templates under `.github/ISSUE_TEMPLATE/`.
2. Fork the repository (or branch, if you have write access) and make your change on a branch.
3. Open a pull request against `main`. The pull-request template asks for what changed and why.
4. `main` is protected: a PR merges only after the whole test stage of
   [`.github/workflows/ci-cd.yml`](.github/workflows/ci-cd.yml) is green and the branch is up to
   date with `main` (enable auto-merge and it lands on its own once that is the case). Nobody pushes
   to `main` directly, not even the maintainer.

## What a pull request needs

- **Conventional Commits.** The version and the changelog are generated from the commit messages
  (`feat:` = minor release, `fix:` = patch release, `build:`/`ci:`/`docs:`/`test:` = no release).
  Squash-merge keeps the PR title as the commit message, so give the PR a Conventional Commit
  title.
- **Tests for new functionality.** New features and bug fixes come with tests in the matching
  project under `tests/` (`StudyLife.Shared.Tests`, `StudyLife.Server.Tests`,
  `StudyLife.Tts.Tests`, `StudyLife.Client.Tests`). A PR that adds behaviour without a test is
  asked to add one. The coverage badge in the README is regenerated from the merged coverage
  report on every release and is expected not to drop (see
  [How the coverage badge is derived](#how-the-coverage-badge-is-derived)).
- **Every defect fix comes with a regression test.** A fix of a defect in the product's
  behaviour (code under `src/`) adds a test that fails without the fix and passes with it.
  Security fixes always get one. Exempt:
  - dependency updates (`fix(deps)`, Dependabot/Renovate PRs, lock-file refreshes) - they are
    covered by the existing test suite and the required CI checks instead;
  - fixes without testable behaviour (docs, CI, build configuration, pure styling/layout) -
    the PR states why no test was added and how the fix was verified.
- **Formatting and warnings.** `dotnet format StudyLife.sln --verify-no-changes` runs as a required
  check (`test-lint`); run `dotnet format StudyLife.sln` before pushing. The build runs with the
  .NET analyzers on and `TreatWarningsAsErrors=true` (`Directory.Build.props`); do not silence
  warnings without saying why in the PR. See [Coding style](#coding-style).
- **Input validation.** New endpoints and DTO fields validate their input (bounds, allowlists)
  and reject anything else with a 400, with a test for the rejection - see
  [`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md#81-input-validation).
- **Developer Certificate of Origin.** By contributing you certify the
  [Developer Certificate of Origin 1.1](https://developercertificate.org/): you wrote the change
  or otherwise have the right to submit it under the project's licence (AGPL-3.0). Sign off
  every commit with `git commit -s`, which adds a `Signed-off-by: Your Name <email>` line.
  Automated dependency PRs (Dependabot, Renovate) are exempt; no check enforces the sign-off.
- **Licence headers.** Every new file states its licence and copyright, see
  [Licence headers](#licence-headers) (checked by `reuse lint` in `test-lint`).
- **All 26 languages.** UI strings live in the resource tables checked by `tools/check-i18n.py`
  (required check `test-i18n`); a new string needs an entry in every language, machine-translated
  is fine, mark it as such.
- **Accessibility.** UI changes follow [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level AA as far
  as the change reaches: semantic elements first (`button`, `a`, `label`, headings) and ARIA only
  where HTML has nothing; every icon-only control has an `aria-label`; everything works with the
  keyboard, and keyboard focus stays visible (the global `:focus-visible` ring in `shared.css`;
  do not remove an outline without replacing it); text and controls keep a contrast of at least
  4.5:1 and 3:1 against their background in light and dark mode; colour is never the only signal;
  animations respect `prefers-reduced-motion` (a global rule in `shared.css` covers CSS, and
  JavaScript animations check the media query); the page `lang` follows the UI language (set in
  `interop.js`). Check new screens with the keyboard and a browser accessibility checker (for
  example Lighthouse or axe DevTools) before opening the PR. No automated accessibility check runs
  in CI yet.
- **API contract.** A change to controllers or DTOs regenerates `docs/api/openapi.json`; commit
  the regenerated file (`test-openapi-contract` fails otherwise) - add-ons and the CLI build
  against it.
- **Database.** Schema changes come with an EF Core migration (`test-ef-migrations` verifies the
  model and the migrations agree).
- **No behaviour behind demo mode.** New endpoints and features must stay read-only in demo mode
  (see `docs/ARCHITECTURE.md`).

### Third-party notices

`THIRD-PARTY-NOTICES.md` lists the third-party software the server runs and sends to the browser, and
the **About** section in Setup shows it to the user. It is generated, never edited by hand. When a
dependency is added, removed or changes its licence or copyright holder:

```bash
python3 tools/check-licences.py --update   # restores the server and the client, rewrites the file
python3 tools/check-licences.py            # what CI runs (test-lint): policy satisfied and file current
```

Commit the regenerated file with the change. A plain version bump of an existing package changes
nothing (the file names packages, licences and holders, not versions). The check fails for a package
whose licence cannot be read (add a `[[verified]]` entry to [`tools/licences/policy.toml`](tools/licences/policy.toml)
only after reading the project's own licence file), for a licence that is not in the policy's
allowed list (add it with its text under `tools/licences/texts/`, or an `[[exception]]` with the
reason), and for any GPL, AGPL or LGPL package - those are not allowed in the build at all. A new
bundled file that is not a NuGet package (a font, a script, a stylesheet) is added as a
`[[component]]` in the policy with its licence text.

### Licence headers

The repository follows the [REUSE Specification](https://reuse.software/): every file states its
licence and copyright in machine-readable form. StudyLife is `AGPL-3.0-only` (licence texts in
[`LICENSES/`](LICENSES/)); the only third-party files are the self-hosted DM Sans and DM Mono
fonts under `src/StudyLife.Client/wwwroot/fonts/` (`OFL-1.1`). A source file starts with two SPDX
tags in its own comment syntax (after a shebang, an `<?xml ...?>` declaration or `<!DOCTYPE html>`):

<!-- REUSE-IgnoreStart -->
```csharp
// SPDX-FileCopyrightText: 2026 Your Name
// SPDX-License-Identifier: AGPL-3.0-only
```

In `.razor` files the tags go into a Razor comment (`@* ... *@`), in CSS into `/* ... */`, in
YAML, Python and shell into `#` lines, in `.csproj` and HTML into `<!-- ... -->`.
<!-- REUSE-IgnoreEnd -->

Visual Studio and Rider add the C# header to new files on their own (`file_header_template` in
[`.editorconfig`](.editorconfig)). Files without comment syntax (Markdown, JSON, images, fonts,
the solution file) and generated files (the EF Core migrations) are covered by
[`REUSE.toml`](REUSE.toml) instead, so a generator's output stays as it writes it. `test-lint`
runs `reuse lint` and fails for a file without either. Locally, with the same pinned version as
CI:

```bash
python3 -m pip install --require-hashes --no-deps -r tools/reuse-requirements.txt
reuse lint
reuse annotate --copyright "Your Name" --license AGPL-3.0-only <file>   # adds a header
```

### How the coverage badge is derived

The badge shows **line and branch coverage** as measured by CI, with no external coverage
service:

1. The `test-unit` job of `.github/workflows/ci-cd.yml` runs the four test projects with
   coverlet (`--collect:"XPlat Code Coverage"`, settings in `coverlet.runsettings`, which drops
   `[GeneratedCode]` such as the JSON source-generation context).
2. `reportgenerator` merges the Cobertura reports with two filters: the `StudyLife.Client`
   assembly (Blazor UI markup) and the EF Core migrations (`StudyLife.Server.Migrations.*`,
   machine-written) are excluded. Everything else - `StudyLife.Server`, `StudyLife.Shared`,
   `StudyLife.Tts`, `StudyLife.Stt` - counts.
3. `scripts/generate_coverage_badge.py` turns that summary into `coverage-badge.json`. Both
   numbers are truncated to one decimal, never rounded up. The same step writes line and branch
   coverage to the job summary of every run, and the coverage including `StudyLife.Client` is
   printed there as well (report only).
4. On a release the lktec-bot App commits that file as `.github/badges/coverage.json`
   together with the image tag bump (the `deploy-bump` job hands it over as an artifact). The README badge reads it through shields.io's `endpoint`
   badge from `raw.githubusercontent.com`, so it shows the numbers of the latest release.

To get the same numbers locally:

```bash
dotnet test StudyLife.sln --collect:"XPlat Code Coverage" --settings coverlet.runsettings
dotnet tool restore
dotnet tool run reportgenerator -reports:"tests/**/TestResults/**/coverage.cobertura.xml" \
  -targetdir:coverage-summary -reporttypes:JsonSummary \
  -assemblyfilters:"-StudyLife.Client" -classfilters:"-StudyLife.Server.Migrations.*"
python3 scripts/generate_coverage_badge.py coverage-summary/Summary.json coverage-badge.json
```

## Running things locally

```bash
dotnet restore StudyLife.sln
dotnet build StudyLife.sln
dotnet test StudyLife.sln
dotnet format StudyLife.sln --verify-no-changes
python3 tools/check-i18n.py
```

See the README's "Development" section for the dev server and the k3d cluster that mirrors
production.

## Coding style

- **C# / Razor:** the [.NET coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)
  and the [.NET runtime C# style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md)
  are the reference. The machine-checked subset lives in [`.editorconfig`](.editorconfig) and is
  enforced by `dotnet format --verify-no-changes` (required check `test-lint`) and the .NET
  analyzers (warnings are errors). Nullable reference types are on; do not suppress nullability
  warnings with `!` without a comment saying why it is safe.
- **Comments explain why, not what.** This codebase documents decisions next to the code (and
  in `docs/`), including the alternatives that were rejected. Keep that up when you change
  behaviour the comment describes.
- **Python** (`tools/`, `scripts/`): [PEP 8](https://peps.python.org/pep-0008/), standard library
  only where possible.
- **Kubernetes YAML:** two-space indentation, every non-obvious setting has a comment; the
  manifests are validated by `kubeconform` (`test-k8s-manifests`).
- **Shell:** POSIX `sh` or `bash` with `set -euo pipefail`.

## Code review

Every change, including the maintainer's own, goes through a pull request and the required
checks. A reviewer (today: the maintainer, see [GOVERNANCE.md](GOVERNANCE.md)) looks at:

1. **Correctness and tests** - does the change do what the PR says, is there a test for the new
   behaviour, and for a defect fix a regression test that fails without the fix (or a stated
   reason why it is exempt, see above)?
2. **Security** - authentication/authorization on new endpoints (`ApiAccess` fallback,
   `ApiKeyScopes` entries), input validation and size limits, no secrets in code or logs, user
   data scoped by `AuthUserId`, outbound URLs through `OutboundUrlPolicy`. Changes to a trust
   boundary update [`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md).
3. **Compatibility** - OpenAPI contract, EF migrations, add-ons and self-hosters; breaking
   changes are marked `feat!:`.
4. **Operations** - Kubernetes manifests, resource limits, rollout order, rollback path.
5. **Docs and i18n** - README/docs updated, all 26 languages present.

Dependency updates from Dependabot/Renovate get the same CI gate; the reviewer checks the
release notes of major-version bumps before merging.

## Governance and roadmap

How decisions are made and who maintains the project: [GOVERNANCE.md](GOVERNANCE.md). Planned
work: [ROADMAP.md](ROADMAP.md).

## Security issues

Please do not open a public issue for a vulnerability - use the private reporting path described
in [SECURITY.md](SECURITY.md).
