# Governance

StudyLife is a **single-maintainer project**. This file says who decides what, how decisions are
made, and what happens when the maintainer is unavailable. It describes how the project works
today. It does not describe a larger team that does not exist.

## Roles

| Role | Who | Responsibilities |
|---|---|---|
| **Maintainer** | [@lukislp](https://github.com/lukislp) (Lukas Körber), the only person with write and admin access | Sets the direction and the [roadmap](ROADMAP.md). Reviews and merges pull requests. Cuts releases (automated, see below). Triages issues and security reports ([SECURITY.md](SECURITY.md)). Enforces the [Code of Conduct](CODE_OF_CONDUCT.md). Administers the repository, its secrets and the production/demo deployments. |
| **Contributor** | Anyone who opens an issue, a discussion or a pull request | Follows [CONTRIBUTING.md](CONTRIBUTING.md) and the Code of Conduct. Contributors do not need write access, since every change arrives as a pull request from a fork. |
| **Automation** (no human authority) | Dependabot, Renovate, semantic-release (`semantic-release-bot`), the `studylife-deploy` key | Propose dependency updates as pull requests, tag releases and publish release notes from Conventional Commits, and write the released image tag into the deployment manifests. None of them can merge to `main` past the required checks. Bot PRs are subject to the same CI gate as human ones. |

There are no other roles today: no co-maintainers, no committee, no release managers. A
contributor who has made sustained, high-quality contributions can be offered co-maintainer
access (see [Becoming a maintainer](#becoming-a-maintainer)). This file gets updated when that
happens.

## How decisions are made

- **Day-to-day changes** (bug fixes, dependency updates, small features) are decided in the pull
  request itself: CI must be green, and the maintainer merges.
- **Bigger changes** (new features, new external dependencies, API or data-model changes,
  anything that affects add-ons or self-hosters) start as an issue or a
  [discussion](https://github.com/lukislp/studylife/discussions), so the direction can be agreed
  before the work starts. Anyone can comment, and the maintainer makes the final call and states
  the reason in the issue.
- **Architecture decisions** get written down next to the code they affect: in
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/SCALING.md`](docs/SCALING.md), and in
  comments on the configuration they explain. The "why" belongs in the repository, not in chat.
- **Security decisions** follow [SECURITY.md](SECURITY.md) and the assurance case in
  [`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md). Accepted residual risks are listed
  there explicitly.
- **Breaking changes** for self-hosters or add-ons are called out in the PR title (`feat!:` /
  `BREAKING CHANGE:` footer), which makes semantic-release cut a major version and puts them at
  the top of the release notes.

## Releases

Releases are fully automated: every merge to `main` whose commits warrant a release
(`feat:`/`fix:`/`refactor:`) gets a version from semantic-release, a GitHub Release with
generated notes, signed container images and a signed `server.zip` (see
[SECURITY.md](SECURITY.md#verifying-releases)). Only the latest release is supported.

## Continuity: what happens if the maintainer is unavailable

Being honest about the bus factor: it is **1**. There is no second person with admin access to
the repository, the container registry namespace or the deployments, and this file does not
pretend otherwise. What exists instead:

- **The code and the history are public.** The repository is AGPL-3.0. Anyone can fork it, and
  the fork keeps the full history, issues stay readable, and every release artifact stays
  downloadable from GitHub Releases and `ghcr.io`.
- **Nothing needed to build or release lives only with the maintainer.** The build is a plain
  `dotnet` + Docker build from this repository ([CONTRIBUTING.md](CONTRIBUTING.md)). Signing uses
  Sigstore keyless signing bound to the GitHub Actions workflow identity, so no private signing
  key would be lost. The CI pipeline is in `.github/workflows/` and runs unchanged in a fork,
  except for the deployment and badge-commit steps, which need the fork's own secrets.
- **Self-hosted instances keep running.** Each installation is independent of the maintainer: no
  licence server, no phone-home, no central account service. Updates stop, but nothing breaks
  at a deadline.
- **No successor arrangement exists.** There is no GitHub account successor, no second owner and
  no organisation account, and none is planned at the moment. Nobody else can take over the
  repository, the `ghcr.io` namespace or the deployments. Continuity therefore rests entirely on
  the points above: the public code, a build anyone can repeat, and keyless signing that a fork
  can set up for its own identity.
- **If the maintainer stops responding** (no reaction to issues, PRs or security reports for
  more than 30 days, with no announcement), users should treat the project as unmaintained. In
  that case: fork it, rebuild from source, and keep the AGPL obligations. If a community fork
  emerges, the maintainer, once reachable again, will link it from the README.

## Becoming a maintainer

There is no formal application. A contributor who has had several non-trivial pull requests
merged, has reviewed others' changes and has shown good judgement on security and compatibility
can be invited by the maintainer. A new maintainer gets write access first, and admin access
only after a while. The new role, and any change to the decision process, is recorded in this
file by pull request.

## Code of Conduct

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md). The maintainer enforces
it.

## Changes to this document

This file is changed like any other: by pull request, merged by the maintainer.
