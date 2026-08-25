# k8s/dev/ - Learning-Cluster Bootstrap Secret

This folder holds `01-secrets.yaml`, the plaintext, publicly-visible test credentials
(`studylife-secrets`, `studylife-pg-app-secret` in namespace `studylife-scale`) used ONLY by the
local Docker Desktop "kind" learning cluster described in `docs/SCALING.md`.

## Why this file is not directly under `k8s/`

Prod uses [Sealed Secrets](https://github.com/bitnami-labs/sealed-secrets) for these exact same
secret names/namespace - see `k8s/sealed-secrets/studylife-scale/studylife-secrets.yaml` and
`studylife-pg-app-secret.yaml`. The plaintext version used to live directly under `k8s/`
(`k8s/01-config-and-secret.yaml`), which meant a bulk `kubectl apply -f k8s/` run against the
prod cluster - by hand, or from muscle memory copied from the learning-cluster instructions -
would silently overwrite the real prod credentials with the public test password. This actually
came close to happening once (see the "near-incident" writeup in `docs/SCALING.md`).

`kubectl apply -f k8s/` is **not recursive**, so it never descends into this folder. That's the
whole point of putting this file here instead of directly under `k8s/`: prod's bulk apply command
can no longer touch it, full stop - no reliance on remembering to skip a specific file.

## How each flow picks this up

- **Learning cluster** (`docs/SCALING.md`, "Testing on Kubernetes"): apply this folder
  explicitly, in addition to the main `k8s/` bulk apply - `kubectl apply -f k8s/dev/`.
- **`k8s/bootstrap-cluster.ps1`**: applies this file explicitly (with the real
  `-PostgresPassword` substituted in place of the `studylife-k8s-dev` placeholder) before its
  main per-file loop over `k8s/*.yaml` - see the script for details. The main loop only ever
  globs `k8s/*.yaml` (no `-Recurse`), so it does not see this folder either; that's deliberate,
  matching the same "subfolder = explicit step" pattern already used for
  `k8s/sealed-secrets/` (see `docs/SCALING.md`, "Sealed Secrets" section).
- **Prod**: never applies this file. Prod's only automated apply path is the Flux
  `Kustomization` at `k8s/flux/05-kustomization.yaml`, which is scoped to
  `k8s/flux/deploy/` (`04-web.yaml`/`05-worker.yaml` only) and never touches `k8s/dev/` either.
  Prod's actual secrets come exclusively from the SealedSecret resources under
  `k8s/sealed-secrets/studylife-scale/`.

## If you add more dev-only secrets later

Keep them in this folder, not directly under `k8s/` - anything with a name/namespace that
overlaps a SealedSecret-managed prod secret must never be reachable by a plain
`kubectl apply -f k8s/`.
