# Roadmap

What the project intends to work on over the next twelve months (until the end of 2027). This is
a statement of direction from the maintainer, not a promise with dates. Priorities can change,
and the reasons get recorded in the linked issues or PRs. Feature requests and comments are
welcome in [Discussions](https://github.com/lukislp/studylife/discussions).

Everything below comes from open items that are already documented in this repository. Each item
links to the place that explains it.

## Security and supply chain

- **Remaining in-cluster plain HTTP.** The calls to studylife-ai, studylife-webhooks and
  studylife-developers use HTTPS with certificates from the internal CA
  ([`docs/SECURITY-ASSURANCE.md`](docs/SECURITY-ASSURANCE.md#83-in-cluster-calls-to-the-add-ons)).
  Still plain: the add-ons' old ports for probes and Prometheus scraping, the gateway → pod hop of
  the developer portal (needs a `BackendTLSPolicy`), and the OTLP export to the collector. Once
  the switch is live, the NetworkPolicies can stop admitting StudyLife on the old add-on ports.
- **Image signature verification at deploy time.** Images are already signed with cosign. Flux
  should refuse unsigned images (see the comment at the signing step in
  `.github/workflows/ci-cd.yml`).
- **Dynamic analysis.** A DAST scan (OWASP ZAP against the container) and fuzzing for the
  parsers that take uploads (ICS import, backup import) should run before releases.
- **Signed release tags.** Tags are lightweight today. Releases are signed through Sigstore
  artifacts, but the tags themselves are not.
- **Redis cluster bus TLS.** This needs a cluster rebuild or `cluster-announce-hostname`, see
  [`docs/SCALING.md`](docs/SCALING.md) "Client TLS".

## Project continuity

- Keep the continuity mitigations in [GOVERNANCE.md](GOVERNANCE.md#continuity-what-happens-if-the-maintainer-is-unavailable)
  true: a public build anyone can repeat, keyless signing, and no secret that only the maintainer
  holds that a fork would need.
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
