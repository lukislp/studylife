# Security Policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's private vulnerability reporting:
https://github.com/lukislp/studylife/security/advisories/new

If you cannot use GitHub, email the maintainer at koerber.lukas@t-online.de with "SECURITY" in
the subject line instead.

Do not open a public issue, discussion or pull request for a security problem. If you are not
sure whether something is a vulnerability, report it privately anyway.

A useful report has the affected version (shown on the Setup page and at
`/api/system/version`), the deployment type (single container, Kubernetes, demo), steps to
reproduce, and the impact you expect.

## What happens after you report

StudyLife has one maintainer (see [GOVERNANCE.md](GOVERNANCE.md)), so these are the targets one
person can actually meet:

| Step | Target |
|---|---|
| Acknowledgement of the report | within **3 days** |
| First assessment (accepted / needs more info / not a vulnerability, with a CVSS severity) | within **14 days** |
| Fix released for **critical** and **high** severity | within **30 days** of acceptance |
| Fix released for **medium** and **low** severity | within **60 days**, or with the next regular release |

The process:

1. **Triage.** The maintainer reproduces the issue in the private advisory, agrees on a severity
   with the reporter, and decides whether it is in scope (see below).
2. **Fix.** The fix is developed in the advisory's private fork or on a branch that does not
   reveal the vulnerability, and it comes with a regression test. It ships through the normal
   pipeline (signed images and `server.zip`).
3. **CVE and advisory.** For accepted vulnerabilities in StudyLife itself, the maintainer
   requests a CVE through GitHub (GitHub is a CVE Numbering Authority). The GitHub Security
   Advisory is published when the fixed release is out. It names the fixed version, and the
   release notes link to it.
4. **Disclosure.** The default is coordinated disclosure: details go public with the advisory,
   once the fix is released or after 90 days, whichever comes first. A different date can be
   agreed with the reporter.
5. **Credit.** Reporters are credited by name or handle in the advisory, unless they ask not to
   be.

If a target slips, the reporter is told why and gets a new date. If you get no acknowledgement
within 7 days, comment on the advisory again or send an email.

## Scope

**In scope:** this repository (server, Blazor client, shared libraries, Dockerfile, Kubernetes
manifests, CI workflows) and the public demo at https://studylife-demo.lktec.org.

**Out of scope here:**
- the add-on repositories (studylife-ai, studylife-hacs, studylife-mcp, ...). Report those to the
  respective project, unless the flaw is on StudyLife's side of the integration;
- publicly known vulnerabilities in third-party dependencies, which Dependabot already tracks;
- findings that need an already-compromised host or cluster node.

Please do not run load tests or automated scanners against the demo.

## Supported versions

Only the latest release receives security fixes, and there are no maintenance branches. The way
to get a fix is to upgrade: database migrations run automatically at startup
([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#migrations)).

## Verifying releases

Every release is signed with [Sigstore](https://www.sigstore.dev/) keyless signing. The signing
identity is this repository's release workflow running on `main`, so there is no long-lived
signing key that could leak. You need
[cosign](https://docs.sigstore.dev/cosign/system_config/installation/) v2 or later.

**Container image** (`ghcr.io/lukislp/studylife-server`):

```sh
cosign verify ghcr.io/lukislp/studylife-server:<version> \
  --certificate-identity-regexp '^https://github\.com/lukislp/studylife/\.github/workflows/ci-cd\.yml@refs/heads/main$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The image also carries an SBOM and SLSA build provenance as BuildKit attestations, which
`docker buildx imagetools inspect ghcr.io/lukislp/studylife-server:<version>` lists.

**`server.zip`** from a [GitHub Release](https://github.com/lukislp/studylife/releases): download
`server.zip` and `server.zip.sigstore.json` from the same release, then run:

```sh
cosign verify-blob server.zip \
  --bundle server.zip.sigstore.json \
  --certificate-identity-regexp '^https://github\.com/lukislp/studylife/\.github/workflows/ci-cd\.yml@refs/heads/main$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The GitHub build provenance for `server.zip` is attached as `server.zip.intoto.jsonl`, and the
GitHub CLI can check it too:

```sh
gh attestation verify server.zip --repo lukislp/studylife
```

If a verification fails, the file was not produced by this repository's pipeline. Do not run
it, and please report it as described above.

## Security design

For what StudyLife protects against, what it does not, and how, see the assurance case in
[`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md) and the "Security" sections of the
[README](README.md#security) and [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#security).
