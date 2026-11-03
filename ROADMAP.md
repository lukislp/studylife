# Roadmap

What the project intends to work on over the next twelve months (until the end of 2027). This is
a statement of direction from the maintainer, not a promise with dates. Priorities can change,
and the reasons get recorded in the linked issues or PRs. Feature requests and comments are
welcome in [Discussions](https://github.com/lukislp/studylife/discussions).

Everything below comes from open items that are already documented in this repository. Each item
links to the place that explains it.

## Security and supply chain

- **TLS for the in-cluster add-on calls.** StudyLife still calls studylife-ai, studylife-webhooks
  and studylife-developers over plain HTTP inside the cluster. A shared secret or HMAC token
  authenticates each call, and NetworkPolicies restrict who can talk to whom. The goal is HTTPS
  with certificates from the internal CA, validated the same way as Redis already is
  ([`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md#83-in-cluster-plain-http-calls)). The
  add-on repositories have to serve TLS first.
- **Image signature verification at deploy time.** Images are already signed with cosign. Flux
  should refuse unsigned images (see the comment at the signing step in
  `.github/workflows/ci-cd.yml`).
- **Dynamic analysis.** A DAST scan (OWASP ZAP against the container) and fuzzing for the
  parsers that take uploads (ICS import, backup import) should run before releases.
- **Signed release tags.** Tags are lightweight today. Releases are signed through Sigstore
  artifacts, but the tags themselves are not.
- **SPDX licence headers** in source files, checked in CI.
- **Redis cluster bus TLS.** This needs a cluster rebuild or `cluster-announce-hostname`, see
  [`docs/SCALING.md`](docs/SCALING.md) "Client TLS".

## Project continuity

- A GitHub account successor for the maintainer account, and moving the repository into an
  organisation with a second trusted owner ([GOVERNANCE.md](GOVERNANCE.md#continuity-what-happens-if-the-maintainer-is-unavailable)).
- Labelled starter issues (`good first issue`) so new contributors have a way in.

## Product

- Keep the feature set described in the [README](README.md) working across the browser/PWA
  client, the native app shell ([studylife-app](https://github.com/lukislp/studylife-app)) and
  the add-ons, with all 26 languages kept complete.
- Finish the studylife-ai `/internal` port cut-over (phase B: remove `/internal/*` from the
  public port), see [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#studylife-ai-integration-servicesaiproxytokenservicecs-servicesaiproxyclientcs-controllersaiproxycontrollercs).
- Keep the public demo at https://studylife-demo.lktec.org current with every release.

## Maintenance (continuous)

- Dependency updates via Dependabot and Renovate, with no known vulnerability left unpatched for
  more than 60 days ([SECURITY.md](SECURITY.md)).
- Keep test coverage at or above the current level (see the badge in the README).
- Keep the documentation in line with the code. The OpenAPI contract is already enforced in CI.
