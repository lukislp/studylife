# Reproducible builds

What can be rebuilt bit for bit from this repository, how to check it, and where the current
gaps are.

## What makes the build repeatable

- **Pinned inputs.** NuGet restores run in locked mode against the committed
  `packages.lock.json` files (`Directory.Build.props`, `RestoreLockedMode` on CI). GitHub
  Actions are pinned by commit SHA, and the Docker base images by digest
  (`src/StudyLife.Server/Dockerfile`).
- **Deterministic compiler output.** `Deterministic=true` is the .NET SDK default, so there are
  no timestamps or random GUIDs in assemblies. On CI (`CI=true`) `Directory.Build.props` also
  sets `ContinuousIntegrationBuild=true`, which maps source paths in PDBs and assemblies to
  `/_/` so the output does not depend on the checkout directory.
- **Same command everywhere.** The release asset `server.zip` is the zipped output of
  ```sh
  dotnet publish src/StudyLife.Server/StudyLife.Server.csproj --configuration Release \
    --runtime linux-x64 --no-self-contained -p:Version=<version> --output publish/amd64
  ```
  run on `ubuntu-latest` with the .NET SDK from `DOTNET_VERSION` in
  `.github/workflows/ci-cd.yml`. The container images copy exactly that output.

## Checking it

`.github/workflows/reproducibility.yml` publishes the server twice from clean trees of the
same commit and compares all ~1700 output files with SHA-256 (`scripts/compare_builds.py`). It
runs weekly, on demand, and on PRs that touch build inputs. Every file must be identical except
the known exceptions listed below. Any new difference fails the run.

To check by hand, run the publish command above twice with `CI=true` (or
`-p:ContinuousIntegrationBuild=true`), cleaning with `git clean -xdf` in between, and then:

```sh
python3 scripts/compare_builds.py <output-1> <output-2>
```

To compare against a release, build the release tag (`git checkout v<version>`) with
`-p:Version=<version>` and compare with the unpacked `server.zip` of that GitHub Release. Before
you compare, verify the download as described in [SECURITY.md](../SECURITY.md#verifying-releases).

## Known gap

On 2026-11-03, two clean publishes of the same commit gave **1685 of 1703 files bit-identical**.
That includes the server's own code, `StudyLife.Shared`, `StudyLife.Tts`, `StudyLife.Stt`,
every third-party assembly, and all static assets except the ones below. The remaining
differences all come from one source:

- **`StudyLife.Client.dll` / `.pdb`** (the Blazor WebAssembly client) differs between two clean
  builds, even at the same path. All of its compiler-generated sources (Razor,
  Toolbelt.Blazor.I18nText, source generators) were compared and are byte-identical, so the
  difference arises inside the client's compilation or WebAssembly build pipeline, not in the
  inputs. This is still being investigated.
- Files that only differ *because of* the client assembly: `StudyLife.Server.dll` / `.pdb` (they
  record the referenced client assembly's MVID), the fingerprinted
  `wwwroot/_framework/StudyLife.Client.<hash>.wasm*` files, `dotnet.js*`,
  `service-worker*.js` and `StudyLife.Server.staticwebassets.endpoints.json`, which carry the
  client's content hash.

A build from a **different checkout path** also differs in the client PDB's document paths
(Razor-generated documents are not covered by the path mapping). For that reason the CI check
builds both times in the same workspace.

**Container images** are not bit-reproducible yet. The `apt-get` layer (espeak-ng, curl)
installs whatever the Debian mirror serves on build day, and layer timestamps are not
normalised (`SOURCE_DATE_EPOCH` is not set). The downloaded voice and model files are already
fixed: they come from commit-pinned Hugging Face URLs and are checked with `sha256sum -c` in the
Dockerfile. The image provenance (SLSA, `mode=max`) records the exact inputs of every
published image, which makes an image auditable even though it cannot be rebuilt bit for bit.

## Next steps

1. Find the source of the client assembly difference (compare the two IL outputs, check the
   WebAssembly SDK steps), then remove the `--known` exceptions from the workflow.
2. Set `SOURCE_DATE_EPOCH` from the commit time for the Docker build, and pin the Debian
   packages through a snapshot mirror.
