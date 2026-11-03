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
  report on every release and is expected not to drop.
- **Every bug fix comes with a regression test.** A `fix:` PR that changes code under `src/`
  adds a test that fails without the fix and passes with it. If a test is genuinely
  impractical (pure CSS/layout, a platform quirk only reproducible on a device), the PR says so
  and describes the manual verification instead. Security fixes always get a regression test.
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
- **All 26 languages.** UI strings live in the resource tables checked by `tools/check-i18n.py`
  (required check `test-i18n`); a new string needs an entry in every language, machine-translated
  is fine, mark it as such.
- **API contract.** A change to controllers or DTOs regenerates `docs/api/openapi.json`; commit
  the regenerated file (`test-openapi-contract` fails otherwise) - add-ons and the CLI build
  against it.
- **Database.** Schema changes come with an EF Core migration (`test-ef-migrations` verifies the
  model and the migrations agree).
- **No behaviour behind demo mode.** New endpoints and features must stay read-only in demo mode
  (see `docs/ARCHITECTURE.md`).

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
   behaviour, and for a bug fix a regression test that fails without the fix?
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
