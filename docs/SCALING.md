# Horizontal Scalability (Learning Branch `feature/scalable-architecture`)

In addition to the existing single-instance setup (SQLite, one process - the default when no
`Database:Provider`/`Cache:Provider`/`Worker:Enabled` overrides are set, see the table below),
this branch makes it possible to run StudyLife as multiple simultaneously running instances
distributed behind a load balancer. **Audit finding O5:** this branch's own K3s/Flux setup has
since BECOME the real production deployment (see "GitLab Integration: Kubernetes Agent + Flux
Image Automation" below for the migration off the old Watchtower-polled single container) - the
single-instance/SQLite CODE PATH itself is still fully supported (single-container `docker run`,
`dotnet run` for local dev), just no longer via a dedicated root `Dockerfile`/`docker-compose.yml`
pair, which were dead weight superseded by the k8s path documented here and removed.

## The Core Idea: Configuration Instead of Two Codebases

Everything is driven via `appsettings`/environment variables, with the default matching today's
behavior:

| Variable | Values | Default | Meaning |
|---|---|---|---|
| `Database:Provider` | `Sqlite` \| `Postgres` | `Sqlite` | single SQLite file vs. Postgres |
| `Database:ConnectionString` | Npgsql connection string | - | only required when `Provider=Postgres` |
| `Cache:Provider` | `Memory` \| `Redis` | `Memory` | process-local vs. distributed |
| `Cache:ConnectionString` | `host:port` | - | only required when `Provider=Redis` |
| `Worker:Enabled` | `true` \| `false` | `true` | is `BackgroundTaskService` (30s tick: push reminders, reports, maintenance) active in this process? |

Docker Compose env var syntax: `Database__Provider` (double underscore instead of `:`, the
standard ASP.NET Core convention).

## What Changed in the Code

- **`StudyLifeDbPostgres`** (`src/StudyLife.Server/Data/StudyLifeDb.cs`): a second DbContext
  subclass used ONLY for Postgres - its own migration history (`Migrations/Postgres/`), because
  SQLite SQL and Postgres SQL aren't interchangeable. The SQLite path stays on the base class
  `StudyLifeDb` itself (all 38 existing migrations are tagged against it - a separate
  `StudyLifeDbSqlite` subclass would have made them invisible to EF Core, which was a real bug
  found live in an earlier version of this branch).
- **Notes full-text search** split behind `INoteSearchStrategy`: SQLite keeps FTS5
  (`SqliteFts5SearchStrategy`, unchanged), Postgres gets `tsvector`/`plainto_tsquery`
  (`PostgresTsvectorSearchStrategy`, language configuration `simple` instead of `german`/`english`
  - a deliberate tradeoff: no stemming, but language-neutral for the multilingual app).
- **Cache** (`CacheHelper.cs`, `SettingsController`/`SessionsController`/`CoursesController`/
  `AuthController`): `IMemoryCache` → `IDistributedCache` (`AddDistributedMemoryCache()` in
  default mode - byte-for-byte the same behavior as before, just behind the interface that also
  serves Redis). The version counters (`SessionHistoryCacheVersion`/`SettingsCacheVersion`) are
  thin facades around `IVersionCounter` (`InMemoryVersionCounter` vs. `RedisVersionCounter`, the
  latter via a real atomic Redis `INCR`).
- **VAPID keys/setup code** (`SystemSecretsService.cs`): switched from file-based
  (`app_data/*.json`) to a single DB row (`SystemSecretsEntity`) - otherwise multiple pods
  without a guaranteed shared volume would each have generated their own, mutually divergent
  values. This also simplifies single-instance operation as a side effect (no more file I/O +
  in-process lock needed).
- **`BackgroundTaskService`**: now only registered optionally (`Worker:Enabled`) - in scaled
  operation it runs exclusively in the separate worker deployment. Additionally, **claim-first
  instead of check-then-act** for reminder dispatch (`TryClaimReminderAsync` in
  `BackgroundTaskService.cs`): `SentReminderEntity` has a unique index on `(AuthUserId, Key)` -
  the claim insert is now committed BEFORE the push is sent instead of after, which turns this
  index into a real distributed lock. Two worker replicas running concurrently and claiming the
  same reminder are therefore guaranteed not to both send the push - whichever loses the insert
  (`DbUpdateException` from the unique constraint) aborts before sending. This makes the worker
  safely replicable (see "Worker Scaling" below) and is a pure code change, no new
  infrastructure.
- **`BackupController`**: the 6 raw DB endpoints (download/restore) respond with
  `501 Not Implemented` in Postgres mode (see scope cuts below); `GET /api/backup/export` (JSON)
  remains available unchanged across providers.

## One Dockerfile: `src/StudyLife.Server/Dockerfile`

**Audit finding O5:** there used to be a second, standalone multi-stage Dockerfile at the repo
root (ran `dotnet restore`/`publish` itself inside the container, no prior publish step needed -
convenient for fast local iteration) - it was never updated after `StudyLife.Tts`/`StudyLife.Stt`
were split out as their own projects, so its `COPY` step (only `Client`/`Server`/`Shared`
`.csproj` files, before `dotnet restore`) silently stopped matching `StudyLife.Server.csproj`'s
actual `ProjectReference`s and `docker build` had been provably broken - `dotnet restore` fails
outright, since `Tts`/`Stt` project files were never copied in. Removed entirely, along with the
matching root `docker-compose.yml`/`setup.sh` single-container-via-Watchtower path it served
(superseded by the k8s path this document covers - see "GitLab Integration" below for the
Watchtower-to-Flux migration on the real cluster).
`src/StudyLife.Server/Dockerfile` - the real production path, used by `docker-server` in
`.github/workflows/ci-cd.yml` - is now the ONLY Dockerfile in the repo, for local testing too (see
below). It expects finished `publish/{amd64,arm64}` folders (a separate `dotnet publish` step,
matching CI's own `publish-server` job) and only copies them in - no `dotnet restore`/`publish` of
its own in the image build.

## Testing Locally: docker-compose.scale.yml

```bash
# 1. Publish, then build the image (src/StudyLife.Server/Dockerfile, see above - the same one CI
#    uses; it expects a prior publish step, hence the separate command below rather than a plain
#    "docker build .").
dotnet publish src/StudyLife.Server/StudyLife.Server.csproj -c Release --runtime linux-x64 \
  --no-self-contained -o publish/amd64
docker build -f src/StudyLife.Server/Dockerfile -t studylife-server:scale-local .

# 2. Start it, scale to 5 server replicas
docker compose -f docker-compose.scale.yml up -d --scale server=5

# 3. Read the setup code from the logs of any server OR worker container (all show
#    the same code - that's exactly the DB-backed consistency this branch establishes)
docker compose -f docker-compose.scale.yml logs server | grep -A2 "Setup-Code"

# 4. Open the app at http://localhost:8090 (nginx load balancer, see nginx-scale/nginx.conf)

# Cleanup (also deletes Postgres/Redis data):
docker compose -f docker-compose.scale.yml down -v
```

## Testing on Kubernetes (Docker Desktop, cluster type "kind")

1. Enable Kubernetes in Docker Desktop: Settings → Kubernetes → Enable Kubernetes, leave the
   cluster type as "kind", Apply & Restart.
2. **Install the CloudNativePG operator** (one-time, cluster-wide, outside the
   `studylife-scale` namespace - needed for the Postgres `Cluster` resource in
   `k8s/02-postgres.yaml`):
   ```bash
   kubectl apply --server-side -f https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/release-1.29/releases/cnpg-1.29.1.yaml
   ```
3. Build the image as above (step 1).
4. **Get the image into the cluster** - the actually tricky part, see the box below.
5. ```bash
   kubectl apply -f k8s/
   # k8s/dev/ (the learning-cluster placeholder Secret) is a SEPARATE, deliberate step - "kubectl
   # apply -f k8s/" above is NOT recursive and never touches it, precisely so a bulk apply of
   # this same command against prod can never clobber the prod SealedSecret-managed secret of
   # the same name/namespace. See k8s/dev/README.md.
   kubectl apply -f k8s/dev/
   # Audit finding O4: k8s/04-web.yaml/05-worker.yaml commit imagePullPolicy: Always (needed for
   # the real registry pull in prod) - this cluster needs Never for the locally-imported image
   # from step 4, so re-apply the two patched back, see "Pitfall" below.
   sed 's/imagePullPolicy: Always/imagePullPolicy: Never/' k8s/04-web.yaml | kubectl apply -f -
   sed 's/imagePullPolicy: Always/imagePullPolicy: Never/' k8s/05-worker.yaml | kubectl apply -f -
   ```
6. `kubectl -n studylife-scale get pods` - wait until everything is `Running` (the Postgres
   cluster takes the longest, `kubectl -n studylife-scale get cluster studylife-pg` shows
   bootstrap progress).
7. **Bootstrap the Redis cluster** (a one-time step, since no Helm is available in the cluster -
   see "Redis Cluster" below for background):
   ```bash
   IPS=$(kubectl -n studylife-scale get pods -l app=redis-cluster -o jsonpath='{range .items[*]}{.status.podIP}:6379 {end}')
   kubectl -n studylife-scale exec -i redis-cluster-0 -- redis-cli --cluster create $IPS --cluster-replicas 1 --cluster-yes
   ```
8. **Install MetalLB** (one-time, cluster-wide - this is what gives `Service type: LoadBalancer`
   an actual IP on bare metal in the first place, see "Stable External IP" below):
   ```bash
   kubectl apply -f https://raw.githubusercontent.com/metallb/metallb/v0.14.9/config/manifests/metallb-native.yaml
   kubectl apply -f k8s/06-metallb-config.yaml
   ```
9. **Install ingress-nginx** (one-time, cluster-wide):
   ```bash
   kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.11.3/deploy/static/provider/cloud/deploy.yaml
   ```
   then run the `kubectl scale`/`kubectl patch` sequence documented in
   `k8s/06b-ingress-nginx-patch.md` (2 replicas + anti-affinity + resource limits +
   `--enable-metrics=true` - NOT via `kubectl apply`, see the comment in the file), then
   `kubectl apply -f k8s/07-ingress.yaml` and optionally `kubectl apply -f k8s/09-uptime-kuma.yaml`.
9b. **Resource governance, pooler, network policies, monitoring** (all purely additive, no
    external credentials needed - the R2 backup in `k8s/02-postgres.yaml` remains deliberately
    commented out until the Cloudflare steps above are done):
    ```bash
    kubectl apply -f k8s/10-pod-disruption-budgets.yaml
    kubectl apply -f k8s/11-pooler.yaml
    # Database__ConnectionString lives in k8s/dev/01-secrets.yaml (moved out of
    # k8s/01-config-and-secret.yaml so a bulk "kubectl apply -f k8s/" can never clobber the prod
    # SealedSecret of the same name, see k8s/dev/README.md) and then points at the pooler - on a
    # fresh cluster, "kubectl apply -f k8s/dev/" is enough as-is with the test password; on an
    # already-running cluster with a rotated password, see the rotation procedure in the
    # "Connection pooler" section below.
    kubectl apply -f k8s/12-network-policies.yaml
    kubectl apply -f k8s/13-monitoring-namespace.yaml
    kubectl apply -f k8s/14-prometheus.yaml -f k8s/15-node-exporter.yaml -f k8s/16-kube-state-metrics.yaml
    kubectl apply -f k8s/17-grafana.yaml
    kubectl apply -f k8s/18-loki.yaml -f k8s/19-promtail.yaml
    ```
10. Setup code: `kubectl -n studylife-scale logs -l app=studylife-web | grep -A2 Setup-Code`
    (or `kubectl -n studylife-scale logs deploy/studylife-worker | grep -A2 Setup-Code`).
11. The app is reachable via the MetalLB IP of the `ingress-nginx-controller` service
    (`kubectl -n ingress-nginx get svc ingress-nginx-controller`) using the host header/DNS name
    configured in `k8s/07-ingress.yaml` - **no longer** via `localhost:8090` (that was purely
    Docker Desktop's special `LoadBalancer` behavior, which goes away once switching to
    `type: ClusterIP` + Ingress). Without a locally resolvable DNS name:
    `curl -k -H "Host: <configured-host>" https://<MetalLB-IP>/`.
12. Clean up: `kubectl delete namespace studylife-scale` (the CNPG operator, MetalLB, and
    ingress-nginx remain installed cluster-wide, but don't affect any other namespaces).

### Pitfall: Local Image + Docker Desktop's "kind" Kubernetes

Unlike older Docker Desktop Kubernetes implementations, the "kind" cluster type has an
**image store that is completely separate from the Docker daemon** - an image built locally via
`docker build` is NOT automatically known to the cluster; `imagePullPolicy: Never` against the
plain image name fails with `ErrImageNeverPull`.

The obvious fix (push/pull via a local registry under `host.docker.internal:PORT`) also fails:
Docker Desktop's "kind" cluster has a built-in registry mirror proxy that forwards pull requests
for EVERY registry (even self-hosted ones) - and it responds for an ad-hoc registry under
`host.docker.internal` with `500 Internal Server Error`.

The reliable approach: import the image directly into the NODE's containerd, via a
`kubectl debug node` pod with access to the host (`/host` mounted):

```bash
# Start a debug pod with host access (stays alive for the next step)
kubectl debug node/desktop-control-plane --image=alpine --attach=false -- sh -c "sleep 3600"
# Wait for/find the pod name
kubectl get pods   # e.g. node-debugger-desktop-control-plane-xxxxx

# Import the image directly into the node's containerd (no registry round-trip needed)
docker save studylife-server:scale-local | \
  kubectl exec -i node-debugger-desktop-control-plane-xxxxx -- chroot /host ctr -n k8s.io images import -

# cleanup
kubectl delete pod node-debugger-desktop-control-plane-xxxxx
```

Once that's done, `image: studylife-server:<tag>` + `imagePullPolicy: Never` means the image lives
directly in containerd, no pull needed. **Audit finding O4:** `k8s/04-web.yaml`/`k8s/05-worker.yaml`
now commit `imagePullPolicy: Always` directly (a direct `kubectl apply` during a prod incident must
never silently fall back to `Never`, see the comment on `k8s/04-web.yaml`) - the exact opposite of
what this "kind" cluster needs for a locally-imported image. Patch it back to `Never` for THIS
cluster only (never committed) when applying those two files, instead of picking them up via the
plain bulk `kubectl apply -f k8s/` in step 5 below:

```bash
sed 's/imagePullPolicy: Always/imagePullPolicy: Never/' k8s/04-web.yaml | kubectl apply -f -
sed 's/imagePullPolicy: Always/imagePullPolicy: Never/' k8s/05-worker.yaml | kubectl apply -f -
```

Run this right after the bulk `kubectl apply -f k8s/` (step 5) - that step still applies the
unpatched, `Always` versions of these same two files first (harmless: at worst a transient
`ImagePullBackOff` until the patched re-apply above lands seconds later, since Kubernetes
converges to whichever version was applied most recently).

This import step must be repeated after every `docker build` with changed code (no automatic
reload). Since `imagePullPolicy: Never` + an unchanged tag is NOT recognized by Kubernetes as a
change (no automatic pod restart), increment the tag on every rebuild (`scale-v2`, `scale-v3`,
...) and keep it consistent across the deployment YAMLs as well as the `docker save`/`ctr import`
step - otherwise pods keep running unnoticed with the old image.

## Bugs Found Live (all fixed, with regression tests)

Nine real bugs reproduced during actual scale-up - exactly the kind of distributed-systems trap
that's easy to miss when reasoning about architecture on paper alone:

1. **The `StudyLifeDbSqlite` subclass made the 38 existing SQLite migrations invisible to EF
   Core** (migration assignment runs off the exact DbContext type, not the base class) - on a
   genuinely fresh DB this would have caused `Migrate()` to create no tables at all. Fix: SQLite
   stays on the base class; only Postgres gets its own subclass.
2. **VAPID keys/setup code: a race between simultaneously starting processes** - the server and
   worker containers start practically at the same time, both saw an empty `SystemSecrets` row
   and EACH generated its own key pair/its own code; whichever write happened last silently
   overwrote the other, while both processes kept their own (now inconsistent) value in memory.
   Fix: an atomic "set only if still empty" SQL update (`UPDATE ... WHERE ... IS NULL`) instead
   of a read-then-write cycle via the EF change tracker.
3. **After the fix from (2), EF Core's identity map returned stale values** -
   `ValidateSetupSecretAsync` queried, via the same `DbContext`, a row that had already been
   tracked (in an earlier call), whose in-memory copy didn't know about the field set via raw
   SQL. Fix: `AsNoTracking()` queries for all reads that need fresh data after a raw SQL write.
4. **nginx's `$host` strips the port** - the load balancer passed `Host: localhost` instead of
   `Host: localhost:8090` through to the backends, which meant the server-side WebAuthn RP
   ID/origin derived from the host header no longer matched the actual client origin ("passkey
   attestation could not be verified"). Fix: use `$http_host` instead of `$host`.
5. **Npgsql expects `timestamp with time zone` to be strictly UTC by default** - the global
   DateTime-kind normalization converter (all DateTime properties in this app are "floating"
   local time with no zone reference) normalizes to `Kind=Unspecified`, which collided with the
   column type Npgsql picks automatically ("Cannot write DateTime with Kind=Unspecified to
   PostgreSQL type 'timestamp with time zone'"). Fix: explicitly set all DateTime columns to
   `timestamp without time zone` in Postgres mode (`StudyLifeDb.OnModelCreating`).
6. **`BackgroundTaskService`'s reminder dispatch wasn't safe for multiple worker replicas under
   the "check-then-act" pattern** - first check whether `SentReminders` already contains the key,
   THEN send, only THEN record the key. Two workers running concurrently could both pass the
   check and both send before one blocked the other on save (an uncaught `DbUpdateException`) -
   too late, since the duplicate push had already gone out. Fix: moved the claim insert BEFORE
   the send (`TryClaimReminderAsync`); the existing unique index on
   `SentReminders(AuthUserId, Key)` thereby becomes a real distributed lock. Verified directly
   against the real Postgres cluster (two parallel `psql` inserts on the same key - exactly one
   commits, the other gets the expected unique-constraint error).
7. **The root `Dockerfile` was pinned to .NET 8** (`mcr.microsoft.com/dotnet/sdk:8.0`/
   `aspnet:8.0`), even though all `.csproj` files have `net10.0` as their `TargetFramework` - a
   genuinely clean (cache-less) build failed with `NETSDK1045`. Likely hadn't been built without
   a Docker build-cache hit since the .NET 10 upgrade. Fix: bumped both base images to `10.0`.
   Also noticed along the way: `dotnet restore` ran against the entire `.sln` (including test
   projects), whose `.csproj` files weren't even copied yet at that point in the multi-stage
   build - fix: restore/publish only `StudyLife.Server.csproj` specifically (the test projects
   don't belong in the image anyway). Also: the newer `aspnet:10.0` base image already ships a
   built-in user with UID 1000, which collided with `useradd -u 1000` - fix: UID 1001.
8. **`docker build` deterministically failed in this environment with `archive/tar: missed
   writing N bytes` / `unexpected EOF`** as soon as the build context reached `tests/`
   directories - reproducible at exactly the same byte boundary, with and without BuildKit, over
   both Bash and PowerShell. Cause presumably OneDrive sync interference on the synced project
   folder (`tests/*/TestResults/` also wasn't covered by `.dockerignore` at all). Fix: added
   `**/TestResults` + `publish/` to `.dockerignore`, AND as a reliable workaround, run the build
   from a copy outside the OneDrive folder if the problem recurs.
9. **The root `Dockerfile` didn't set `TZ` - containers ran on UTC instead of Berlin local
   time.** The whole app treats `DateTime.Now` as "floating" local wall-clock time (see
   `docs/ARCHITECTURE.md`); `src/StudyLife.Server/Dockerfile` (the production path) therefore
   sets `ENV TZ=Europe/Berlin`, but the root Dockerfile (this branch, the local test path) never
   had that - in summer (CEST, UTC+2) this meant all K3s pods ran 2 hours "in the past". Noticed
   live when a session entered for 20:40 real local time didn't trigger a push reminder: the test
   push function itself worked (VAPID/subscription/browser permissions all fine), but the worker
   thought "now" was 18:4x instead of 20:4x, incorrectly treating the session as ~2 hours away.
   Fix: added `ENV TZ=Europe/Berlin` to the root Dockerfile, verified via `date` inside the
   container (`CEST` instead of `UTC` in the output).

All nine were found **empirically** through the actual docker-compose or Kubernetes setup (not
through code review) - proof of why "actually scale up and test it once" was indispensable for
this learning goal. Verified with 22 real HTTP requests (registration + 20× login) distributed
across 5 replicas, both against docker-compose and against the real Kubernetes cluster - 22/22
successful both times; the later worker/Redis/Postgres HA round was additionally verified via a
dedicated `ApiVerify` script (registration + read/write/cache consistency across 5 pods with a
forced reconnect per request) as well as a forced Postgres primary failover followed by a
complete re-test.

## Worker Scaling (HPA autoscaling, real work partitioning, cloud-portable)

`k8s/05-worker.yaml` is a normal `Deployment` (NOT a `StatefulSet` - see below for why that's not
needed), WITHOUT its own `spec.replicas` field (see the HPA section further below for why that's
deliberate). Several complementary mechanisms work together here:

- **Dynamic shard claim via Redis instead of pod names (real scaling, platform-independent).**
  Each worker replica now sweeps only its own user partition: `AuthUserId % ReplicaCount ==
  Shard`. The shard is claimed dynamically AT RUNTIME (`IWorkerShardClaim`/
  `RedisWorkerShardClaim`, `src/StudyLife.Server/Services/`): each process gets a random instance
  ID on startup, and on every 30s tick tries to claim slots `0..ReplicaCount-1` in order using
  `SET worker:shard:{i} <instance-id> NX PX 90s` (a single key per Redis call, cluster-safe
  without hash tags), renewing the TTL on a slot it has already won. Deliberately **no more
  reliance on pod names/hostnames** (the first version of this mechanism derived the shard from
  the stable `StatefulSet` pod name - which only works on Kubernetes; AWS ECS Fargate, for
  example, doesn't assign sequential task IDs). The Redis claim only needs a reachable Redis
  connection (required anyway for `Cache:Provider=Redis` in multi-worker operation).
- **`ReplicaCount` now comes live via `IWorkerReplicaCountProvider` instead of a value frozen at
  pod startup** (`src/StudyLife.Server/Services/IWorkerReplicaCountProvider.cs`) - this was a
  deliberate rework to make HPA autoscaling work safely (see the HPA section below for the
  "why"). Two implementations:
  - `StaticWorkerReplicaCountProvider` - a fixed value read from `Worker:ReplicaCount`
    (VPS/docker-compose or Kubernetes operation WITHOUT HPA, where `spec.replicas` still has to
    be kept manually in sync with `Worker:ReplicaCount`).
  - `KubernetesWorkerReplicaCountProvider` (`Worker:ReplicaCountSource=Kubernetes`) - queries the
    scale subresource of its own deployment live via the Kubernetes API (a raw `HttpClient` +
    the ServiceAccount identity mounted in the pod, instead of a full Kubernetes client NuGet
    package - which would be a disproportionately heavy dependency for a single GET query).
    Fail-safe instead of fail-fast: if the API query fails (network, RBAC), the last known value
    keeps being used, never 0/1 as a panic reaction. `IWorkerShardClaim` returns the ordinal AND
    the underlying replica count from THE SAME claim call (the `LastReplicaCount` property), so
    both values are guaranteed to stay consistent within one tick even if the replica count
    changes mid-tick.
  RBAC (`k8s/05b-worker-rbac.yaml`): a dedicated `studylife-worker` ServiceAccount, with a Role
  granting EXACTLY one permission (`get` on `deployments/scale`, `resourceNames:
  [studylife-worker]`) - no access to the full deployment object, no write permissions.
- **Claim-first remains as a safety net** (`TryClaimReminderAsync`, see above) for the brief
  transition window where two processes, due to a lease delay, briefly believe they hold the
  same shard (e.g., during a rolling update or an HPA scale event) - the unique index on
  `SentReminders(AuthUserId, Key)` still prevents a duplicate send in that case.
- **Why no Kafka for this split?** Kafka is built for continuous event streams with consumer
  groups; `BackgroundTaskService` is a periodic sweep with a tiny message volume. A Kafka cluster
  (broker + coordination + a new `Confluent.Kafka` client) would be a disproportionately large
  new piece of infrastructure for this problem - the Redis shard claim plus the already-existing
  unique index on `SentReminders` solve the same task (distributed, exactly-once delivery AND
  partitioned work per reminder) using infrastructure that's already there for caching anyway.
- **Single-instance operation (Pi/docker-compose.yml) unchanged.** `Worker:ReplicaCount` default
  `1` → `StaticWorkerShardClaim` (always shard 0, no Redis call) - byte-for-byte the same
  behavior as before this round of changes.

### HorizontalPodAutoscaler (HPA) for the Worker

`k8s/05c-worker-hpa.yaml` - CPU-based (the only metric type that needs no additional metrics
adapter, since K3s already ships `metrics-server`), `minReplicas: 1`, `maxReplicas: 4`,
`averageUtilization: 70`. More cautious scale-down than the aggressive default
(`stabilizationWindowSeconds: 300`), so the worker doesn't immediately shrink again after every
short load spike ends (flapping on the tight Pi budget).

**Why this simply wasn't possible before**: `Worker:ReplicaCount` was a value read from an env
var at pod startup, FIXED for the entire lifetime of the process, used both for the Redis shard
claims and for the user partitioning itself (`AuthUserId % ReplicaCount == Ordinal`). An HPA
would have left this value stale: if the HPA scales down from, say, 5 to 2 pods, the 2 remaining
pods would keep thinking "there are 5 shards", and 3 of the 5 user segments would simply never
be processed again (reminder/report generation for those users silently stops, without it being
noticed - not an error, just silent non-processing). The `IWorkerReplicaCountProvider` rework
above fixes this structurally: the shard size now follows the actual pod count live.

**`spec.replicas` deliberately NOT in the deployment manifest** (`k8s/05-worker.yaml`): it would
compete with the HPA over this field on every Flux reconcile (server-side apply) - a
field-ownership conflict - and would reset the replica count the HPA had just set. Without this
field, Kubernetes automatically creates 1 replica on first creation, after which the HPA takes
over completely.

**Verified live** (a real rollout on prod, 3 running pods): all 3 distinct shard ordinals (0, 1,
2) correctly claimed in Redis with 3 distinct instance IDs, no warnings about the Kubernetes API
in the pod logs, `kubectl auth can-i get deployments.apps/studylife-worker --subresource=scale
--as=system:serviceaccount:studylife-scale:studylife-worker` confirms the RBAC permission.

**Pitfall found live while rolling out** (a process issue, not a code issue): changes to
`k8s/05-worker.yaml` applied manually via `kubectl apply` were reverted again by the Flux
reconcile because they hadn't been committed+pushed yet (Flux pulls from Git, not from the local
working directory) - this briefly caused pods to run again with the old `default` ServiceAccount.
Order of operations for manual K8s changes to Flux-managed files: ALWAYS commit+push first, only
then `kubectl apply` for immediate effect, never the other way around (this reinforces the
already-existing Git/Flux race-condition lesson from the GitLab integration above).

### HorizontalPodAutoscaler (HPA) for Web

`k8s/04c-web-hpa.yaml` - the same CPU-based configuration as the worker (`minReplicas: 1`,
`maxReplicas: 4`, `averageUtilization: 70`, cautious scale-down with a 300s stabilization
window). Considerably simpler than the worker: Web is stateless, and any pod can answer any
incoming request - no user partitioning, no `IWorkerReplicaCountProvider` equivalent needed.
`spec.replicas` removed from `k8s/04-web.yaml` (same field-ownership reason as the worker).
**A deliberate tradeoff**: `minReplicas: 1` instead of the previous fixed 2 - "scaling down" only
becomes visible/testable with a genuine lower bound, but this costs the previous guarantee of
"always 1 pod per node" at very low load (anti-affinity still applies, but has no effect with
only 1 replica).

**Verified live in both directions, without needing an artificial load test**: the rollout itself
(a Flux image update coincided in time with the removal of `replicas:`) triggered a genuine
restart of all pods - the ASP.NET Core startup CPU spike (JIT compilation/warmup) of the new pods
organically drove the HPA up to 4 replicas (`SuccessfulRescale: cpu resource utilization above
target`). Afterward, observed: CPU settled at 14-30% (well below the 70% target), and after the
5-minute stabilization window elapsed, the HPA scaled back down to 2 replicas on its own - Web
remained continuously reachable throughout (`HTTP 200`, no pod-outage window due to the
rolling-update strategy).

**Additionally tested separately** (direct CPU stress in the worker pod, several `yes >
/dev/null` started in parallel, for 100s): the worker scaled from 1 to 4 replicas in ~30s (CPU
target exceeded 8-fold), and immediately returned to normal CPU values once the load ended, with
scale-down following automatically after the stabilization window. Confirms: the HPA mechanism
itself works reliably in both directions, regardless of whether the load comes from real traffic
or synthetic CPU pressure.

**A side finding discovered live while testing**: a load test against `studylife-web` **via
ingress-nginx** (rather than directly against the service) first hits the new rate limiting
(`limit-rps: "20"`, see the Ingress section above) - with a burst of 2000 parallel requests, only
480 got through, and the rest correctly got `503` from the rate limiter before ever reaching the
pods. To do a real capacity test of the pods themselves, you have to test directly against the
ClusterIP service (bypassing the rate limiter) - which additionally revealed that the root page
(`/`) is too lightweight to meaningfully load the pods (~15m CPU out of a 500m limit even under
high parallelism) - the organic rollout spike was the more informative test.

### HorizontalPodAutoscaler (HPA) for NGINX Gateway Fabric

Unlike Web/Worker, there's no dedicated `HorizontalPodAutoscaler` object here; instead, the
native `autoscaling` field in `NginxProxy.spec.kubernetes.deployment` (`k8s/07c-gateway.yaml`) is
used - NGF's own `provisioner` deployment controller keeps `.spec.replicas` on the data plane
continuously in sync with this CRD (confirmed via a live test: a config change immediately
triggered a rolling restart followed by resynchronization). An additional, externally created
HPA object would have competed with the controller over exactly this field - the same
field-ownership conflict as originally with Web/Worker before removing `spec.replicas`, except
this time it can't be resolved by removing a field, because NGF itself is the owner. When
`autoscaling.enable: true` is set, NGF creates the HPA object itself (`kubectl -n nginx-gateway
get hpa` shows it afterward, just like any other HPA).

`minReplicas: 2` (not 1 like Web) - deliberately different from Web/Worker: the soft
anti-affinity between data-plane pods spreads them across nodes, which would be pointless with
only 1 replica, and the data plane is the only path for all incoming traffic (no single node
failure should ever make all 4 public services unreachable at once). `maxReplicas: 4`,
`targetCPUUtilizationPercentage: 70`, `scaleDown.stabilizationWindowSeconds: 300` - identical
values to Web/Worker, for consistency.

**Verified live after the rollout**: `kubectl -n nginx-gateway get hpa` showed `cpu: 6%/70%,
MINPODS 2, MAXPODS 4, REPLICAS 2` (no fluctuation caused by the switch itself), the rollout
proceeded with no reachability gap (`curl` against the ClusterIP service with `--resolve`
immediately afterward: `200`), and the PDB (`studylife-gateway-nginx`, `minAvailable: 1`) remains
meaningfully effective with `minReplicas: 2`.

## Redis Cluster

`k8s/03-redis.yaml` is a real Redis Cluster (3 masters + 3 replicas, hash slots distributed
across all 16384 slots) instead of a single instance - hand-built as a `StatefulSet`, since no
Helm was available in the cluster (functionally identical to the usual Bitnami chart). The
bootstrap step (`redis-cli --cluster create ... --cluster-replicas 1`) is one-time and not part
of `kubectl apply` - see step 7 of the Kubernetes guide above.

No code differences from the single-instance case are needed: all Redis usage in this app is
single-key (`IDistributedCache` Get/Set per cache key in `CacheHelper.cs`,
`RedisVersionCounter` as a plain `INCR`/`GET` per key) - exactly what cluster slots can represent
without cross-slot operations. `StackExchange.Redis` detects the cluster topology itself on
connection setup (`CLUSTER SLOTS`); `Cache__ConnectionString` only lists all node addresses for
the initial connection (`k8s/01-config-and-secret.yaml`).

### Client TLS (App↔Redis) - Cluster Bus Deliberately Still Plaintext

The App↔Redis hop (where real cache/session/shard-claim data flows) is TLS-encrypted: a new
`Certificate redis-tls` (one certificate for all 6 pods, DNS names + wildcard SAN, from the
in-cluster CA), `tls-port 6380` in addition to the existing plaintext port 6379 (dual-stack, NOT
TLS-only). `Program.cs`: `ConfigurationOptions.Parse` + a `CertificateValidation` callback
(accepts any certificate as long as `ssl=true` is set) - the same tradeoff as with
ingress-nginx→backend pods: the goal is encryption against eavesdropping on the cluster network,
not protection against a compromised pod. The `NetworkPolicy` was extended to cover port 6380.

**The cluster bus (Redis↔Redis gossip/replication) deliberately remains plaintext** - tested live
locally and then discarded: `tls-cluster yes` via a rolling update across all 6 pods reliably
breaks the gossip protocol in this hand-built, pod-IP-based cluster setup (no
`cluster-announce-hostname`, which would only be available with a newer Redis version plus
additional config):

- `cluster_state` flipped to `fail` during/after the rolling restart, and `cluster_slots_fail`
  showed a full master's share (5461 out of 16384 slots) as down.
- `cluster nodes` afterward permanently showed stale IP AND bus-port entries for the peers
  (`@16379` instead of `@16380` on some nodes) - no self-healing even after several minutes of
  waiting, because `nodes.conf` persists this (now incorrect) state and the broken gossip
  connection itself could no longer propagate a correction.
- Recovery required a complete cluster rebuild (delete PVCs, recreate the StatefulSet, run
  `redis-cli --cluster create` again) - uncritical on the local test cluster (only test data),
  but on prod with real user data this would have been a genuine incident.

**Deliberate decision**: cluster-bus TLS was not implemented. The bus is exclusively
in-cluster traffic between the `redis-cluster` pods themselves anyway (already tightly restricted
to exactly these pods by the `NetworkPolicy`) - the goal actually worth protecting (encrypting
the App↔Redis hop, where real payload data flows) is already achieved with the client TLS above.
A future implementation would need either a Redis upgrade with `cluster-announce-hostname`
support, or a complete cluster rebuild with TLS from the start (real downtime), not just a
rolling update on an existing cluster.

**A near-incident found live (an important lesson)**: while updating
`Cache__ConnectionString` (part of the same file as the secrets), the full
`k8s/01-config-and-secret.yaml` was accidentally applied directly to prod via `kubectl apply` -
but this file contains placeholder passwords (`stringData: password: "studylife-k8s-dev"`) for
`studylife-secrets`/`studylife-pg-app-secret`, which are normally replaced ONLY by
`bootstrap-cluster.ps1` at apply time with real values managed via Sealed Secrets. This briefly
overwrote both secrets with the placeholder (immediately detected via a byte-length comparison -
17 bytes instead of 24/125). Fortunately, CNPG hadn't yet propagated the password change to the
actual database (authentication with the placeholder failed) - immediately reset to the real
value via a SealedSecret delete+reapply (the same trick used to force adoption, see the
"Sealed Secrets" section), 0 pod restarts during the entire incident. **Lesson**: NEVER apply
`k8s/01-config-and-secret.yaml` in full to prod, not even for a seemingly harmless ConfigMap
change in the same file - work with targeted `kubectl patch`/`kubectl set env`/Sealed-Secret
reapply, exactly as with the other placeholder files (`07-ingress.yaml`, `17-grafana.yaml`,
`09-uptime-kuma.yaml`).

### Prod Secret vs. Learning-Cluster Placeholder (structural fix)

The near-incident above was possible because the placeholder `Secret`s lived directly under
`k8s/`, under the SAME name/namespace as the prod SealedSecret-managed secrets - so ANY bulk
`kubectl apply -f k8s/` against prod (not just editing `Cache__ConnectionString`) could have
overwritten them. Since then, the two `Secret`s (`studylife-secrets`, `studylife-pg-app-secret`)
have moved to their own `k8s/dev/01-secrets.yaml` (see `k8s/dev/README.md`) - `kubectl apply -f
k8s/` is not recursive, so the collision is now structurally impossible, not just a documented
warning to remember. The `ConfigMap` (`studylife-config`, no credentials) stays directly under
`k8s/` in `k8s/01-config-and-secret.yaml`, shared by both flows. This doesn't replace the
"targeted patch, never full-file apply" discipline for the other placeholder files below (their
placeholders are hostnames/scrape-configs, not credentials with a same-name prod counterpart, so
the same subfolder trick doesn't directly apply) - it only closes this specific, highest-severity
collision.

**This exact mistake actually happened later (no longer a near-incident), despite the warning
above**: `k8s/17-grafana.yaml`/`k8s/09-uptime-kuma.yaml` were applied directly via
`kubectl apply -f` for an `automountServiceAccountToken` fix - both files thereby overwrote the
real ingress hostnames with the placeholders (`grafana.example.invalid`/`uptime.example.invalid`),
and cert-manager reacted to the changed ingress annotation and IMMEDIATELY reissued certificates
for the wrong (placeholder) hostnames. Grafana and Uptime Kuma thereby became unreachable for all
users (unlike the secrets incident above: this time there was a real, brief outage, not just a
near-miss). Fix: the real hostnames could NOT be reconstructed from the cluster (the old
certificates had already been overwritten) - asked the user, then fixed it in a targeted way via
`kubectl patch ingress --type=json`; cert-manager then automatically reissued the certificates
for the correct hostnames (via ingress-shim). Both files now have an impossible-to-miss warning
comment right at the top of the file. **Reinforced lesson**: a comment buried in a different file
isn't enough of a reminder - the warning has to live in EVERY affected placeholder file itself,
right where you actually see it while editing.

## Postgres HA Cluster (CloudNativePG)

`k8s/02-postgres.yaml` is now a CloudNativePG `Cluster` resource (1 primary + 2 replicas,
streaming replication, automatic failover) instead of a single pod - the current standard
approach for Postgres HA on Kubernetes, conceptually comparable to what RDS Multi-AZ does
internally. The operator itself is a one-time, cluster-wide installation step (step 2 of the
Kubernetes guide above) outside the `studylife-scale` namespace.

CNPG automatically creates three services: `studylife-pg-rw` (always the current primary, stays
correct even after a failover), `studylife-pg-ro` (replicas, load-balanced), and `studylife-pg-r`
(any instance). The app deliberately uses **only** `-rw` for ALL requests (Web + Worker) - the
replicas serve exclusively for failure resilience/durability, not read capacity. Real
read/write splitting via `-ro` would be possible, but would require its own routing logic in the
controllers and a decision on replication lag/read-after-your-own-write consistency - deliberately
not part of this plan.

For this data volume (a study tracker, not millions of rows), sharding (Citus/CockroachDB) was
deliberately NOT used - a 1-primary-N-replica HA topology fully covers the actual need (failure
resilience); sharding solves a different problem (write throughput/data volume beyond a single
node) that this app doesn't have.

**Failover verified empirically**: deleted the current primary pod (`kubectl delete pod
<primary>`), and CNPG automatically promoted a replica and reattached the old primary as a fresh
replica, with no manual intervention. The complete cycle (old primary terminates → new primary
promoted → old primary re-syncs as a replica) took around 2-3 minutes on the local Docker Desktop
cluster (a single physical node sharing CPU/disk with the Web, Worker, Redis, and all three
Postgres pods) - most of that time is the old primary's graceful-shutdown checkpoint plus the
resync of the rejoin, not the actual failover decision itself. The `ApiVerify` script ran fully
again immediately afterward: all 5 Web pods answer identically, row counts unchanged before/after
the failover - no data loss.

### Operator Upgrade 1.24.0 → 1.29.1 (EOL + critical CVE)

1.24.0 already reached its end-of-life in May 2025, and versions before 1.29.1 have a
**critical security vulnerability** (CVE-2026-44477, CVSS 9.4) as well as three independent HA
failover bugs (including a label-retention issue that could have caused writes to be incorrectly
routed to the old primary during a network split) - not an optional "we could get to this
sometime", but an overdue security update.

CNPG's own documentation recommends upgrading sequentially through every minor version instead of
jumping - **tested live to see whether a direct jump works anyway** (first locally in the Docker
Desktop cluster, then identically on prod): `kubectl apply --server-side` of the new operator
manifest, CRDs updated without issue (3 new resource types were added: `databases`,
`publications`, `subscriptions`, `failoverquorums`, `clusterimagecatalogs`), operator pod rollout
uneventful. The running `Cluster` reacted with a brief "Primary instance is being restarted
without a switchover" - not a real failover, just an in-place restart of the instance manager
(considerably less disruptive than the CNPG docs describe as standard behavior) - the cluster was
back to "Cluster in healthy state" seconds later, 0 pod restarts over the entire observation
period, WAL archiving/last backup stayed `True`/`Succeeded`, replication remained `streaming`.
**So the direct version jump worked flawlessly for our setup** - the "upgrade sequentially"
recommendation is a general precaution, not a hard requirement, at least not in this case.

The pgbouncer image was automatically bumped to `1.25.1` by the operator upgrade as well - the
known pooler metrics gap (port 9127, see "Deliberate Scope Cuts") persists unchanged even with
this newer version (no listener in `/proc/net/tcp`, verified live).

**A deprecation warning found live** (noticed on the next `kubectl apply -f k8s/02-postgres.yaml`
after the operator upgrade, not part of this change): "Native support for Barman Cloud backups
and recovery is deprecated and will be completely removed in CloudNativePG 1.30.0. Found usage
in: spec.backup.barmanObjectStore." - our R2 backup path (see "Postgres Backups to Cloudflare
R2") uses exactly this field. Before any future upgrade to 1.30.x, this needs to be migrated to
the new "Barman Cloud Plugin", otherwise the backup configuration breaks. Not yet implemented,
noted here only as a known future task.

### Metrics Endpoint TLS-Encrypted

`spec.monitoring.tls.enabled: true` - CNPG generates the server certificate for port 9187 itself
(the same internal CA mechanism as for Postgres client connections), no manual
certificate/cert-manager object needed - the only Prometheus scrape job in this round that
natively supports TLS without a sidecar/proxy. Changing this option triggers a rollout of all
instances according to the CNPG docs - verified locally and on prod, the cluster stayed
continuously "healthy" (only the known brief in-place restart as with the operator upgrade
above), all 3 targets show `up` over HTTPS in Prometheus. The scrape config
(`k8s/14-prometheus.yaml`) was changed accordingly to `scheme: https` +
`insecure_skip_verify: true` (the same tradeoff as everywhere else: encryption against
eavesdropping on the cluster network, not certificate validation against a CA Prometheus doesn't
know).

**Deliberately NOT changed**: the remaining scrape targets (`redis_exporter`, ingress-nginx
metrics, node-exporter, kube-state-metrics, the external NPM `nginx-prometheus-exporter`) - none
of these natively support TLS without a sidecar/reverse-proxy in front of the respective
exporter, which for pure metrics data with no payload/secret exposure would be an unfavorable
effort-to-benefit ratio (Prometheus itself already runs in a `monitoring` namespace already
tightly restricted via NetworkPolicy anyway).

## CI Validation of the Scaling Artifacts

`.gitlab-ci.yml` has two purely validating jobs in the `test` stage - they run on every push/MR
like the other `test:*` jobs, need no real cluster/registry credentials, and trigger NO
deployment:

- **`test:k8s-manifests`**: [`kubeconform`](https://github.com/yannh/kubeconform) validates
  `k8s/*.yaml` offline against bundled OpenAPI schemas (`-ignore-missing-schemas` skips the
  CloudNativePG `Cluster` CRD for lack of a bundled schema, instead of treating it as an error -
  the 9 remaining standard K8s objects are genuinely validated). Deliberately NOT
  `kubectl apply --dry-run=client`: despite `--validate=false`, this still needs API discovery
  against a real server and fails in CI with no cluster connection at all - tested and confirmed
  locally before this decision was made.
- **`test:compose-scale`**: `docker compose -f docker-compose.scale.yml config` (a pure
  syntax/interpolation check, no actual startup).

No new image-build job needed: `docker:server` (uses `src/StudyLife.Server/Dockerfile`, see
above) already builds the single image that works equally for SQLite/Postgres, Memory/Redis,
single-/multi-worker - it's all runtime configuration, no build difference. The new files
(`IWorkerShardClaim` and others) are automatically covered by the existing `test:unit` job.
Automatic deployment (`kubectl apply` against a real cluster) is deliberately NOT part of this
pipeline extension.

**Container scanning (`trivy:server`, new)**: originally left out on purpose (pure home-network
setup, no internet exposure) - that reasoning no longer holds now that studylife-web also sits
behind a public domain via NPM/MetalLB. Runs after `docker:server` against the freshly pushed
image (`docker pull` + `aquasec/trivy:latest image`), `--exit-code 0` (purely informational,
doesn't block the pipeline) - most findings would be CVEs in the Microsoft base image itself
anyway, which only a base-image update can fix, so no release should be unexpectedly blocked
because of it. The same check (informational, `docker pull` + `aquasec/trivy:latest`) runs for
piwatch in `build-and-push.ps1` itself, because that separate repo has no CI of its own.

## Hardware Sizing for a Multi-Node Bare-Metal Cluster (e.g., 4× Raspberry Pi)

**8GB modules recommended, 16GB unnecessary.** Rough calculation for the current pod count (5
Web + 3 Worker + 3 Postgres + 6 Redis + 2 Ingress + 2 MetalLB + 1 Uptime Kuma ≈ 22 pods): app
workloads together roughly 3-4 GB RAM cluster-wide, plus Kubernetes' OWN overhead (kubelet,
containerd, CNI, kube-proxy) - typically 300-500 MB PER NODE for a full `kubeadm` Kubernetes, so
1.2-2 GB across 4 nodes just for the platform itself. At 4 GB per Pi, after Kubernetes overhead +
OS there's barely any room left for real load spikes or a metrics pipeline (Grafana Alloy agent
or similar, see below); 8 GB per Pi gives a noticeable buffer, without the actual app (a study
tracker with minimal data volume) ever coming close to exhausting it. 16 GB modules would be pure
overprovisioning for this data volume.

**Additional recommendation, independent of the RAM choice**: use [K3s](https://k3s.io/) instead
of full `kubeadm` Kubernetes - the de facto standard for Raspberry Pi clusters, noticeably
lighter-weight (a single binary, embedded SQLite instead of full etcd, fewer control-plane
components), which further reduces the per-node overhead significantly.

## Pod Anti-Affinity (works with 1 node AND N nodes)

`k8s/03-redis.yaml`/`04-web.yaml`/`05-worker.yaml` as well as `k8s/02-postgres.yaml` (CNPG) now
have `podAntiAffinity` with `preferredDuringSchedulingIgnoredDuringExecution` (soft), NOT
`required` (hard). A hard anti-affinity would cause replica 2+ to remain `Pending` forever on a
single-node cluster (like this Docker Desktop test setup) - there's no second node to move to.
With `preferred`, the scheduler actively tries to spread across multiple nodes (weight 100,
`topologyKey: kubernetes.io/hostname`), but falls back gracefully to co-location instead of
blocking when there's only one node - verified locally: after the rollout all pods stayed
`Running`, none `Pending`.

CNPG (`k8s/02-postgres.yaml`) enables pod anti-affinity between its own instances by default on
its own (`enablePodAntiAffinity: true`, `podAntiAffinityType: preferred`) - set explicitly here
anyway instead of relying on the default, for documentation purposes and as protection against
future CNPG version changes.

For the 6 Redis pods, the anti-affinity additionally makes the scheduler try NOT to place a
master and its corresponding replica (from `redis-cli --cluster create --cluster-replicas 1`) on
the same node - otherwise a node failure would hit master+replica at the same time, and Redis'
own redundancy would be useless for that slot range.

**Limit of this verification**: on the current single-node test cluster, it's only possible to
verify that scheduling behavior does NOT get worse - real cross-node spreading can only be
genuinely verified on an actual multi-node cluster (`kubectl get pods -o wide`, checking that,
e.g., no 2 Web replicas share the same `NODE` value, as long as enough nodes are available).

## Stable External IP (MetalLB) + Ingress Controller + End-to-End TLS

### MetalLB

Without a cloud provider, there's no one to assign a real IP to `Service type: LoadBalancer` -
that's exactly what [MetalLB](https://metallb.io/) handles on bare metal. Deployed in
**Layer 2 mode** (ARP-based, no BGP-capable router needed - the standard choice for homelab
clusters).

- Installation (a one-time, cluster-wide step, analogous to the CNPG operator):
  `kubectl apply -f https://raw.githubusercontent.com/metallb/metallb/v0.14.9/config/manifests/metallb-native.yaml`.
- `k8s/06-metallb-config.yaml`: `IPAddressPool` + `L2Advertisement`. The IP range in there is
  **only for the local Docker Desktop test cluster** (`172.18.255.200-250`, from the `kind`
  Docker network) - for the real multi-node cluster it needs to be adjusted to the actual
  home-network subnet (a small, fixed reserved range that the DHCP server does NOT hand out).
- **Direct answer to "what does the external network proxy additionally need when the cluster LB
  itself is clustered"**: nothing running. MetalLB gives the ingress service ONE fixed IP, which
  moves to another node via ARP on a node failure - the external proxy configures this ONE IP as
  its upstream, just as it presumably configures the Pi's fixed IP today. How many
  ingress/app pods/nodes actually run behind it stays invisible to the external proxy; verified
  locally that MetalLB immediately assigns the `ingress-nginx-controller` service an IP from the
  pool (`EXTERNAL-IP` in `kubectl get svc -n ingress-nginx`).

### Ingress Controller Instead of a Bare LoadBalancer Service

`k8s/04-web.yaml`'s service is now `type: ClusterIP` (previously `LoadBalancer`, which was just
Docker Desktop's special behavior) - external reachability + TLS termination are now handled by
[ingress-nginx](https://kubernetes.github.io/ingress-nginx/) in front of it:

- Installation: the official manifest (`controller-v1.11.3/deploy/static/provider/cloud/deploy.yaml`),
  then the `kubectl scale`/`kubectl patch` sequence documented in
  `k8s/06b-ingress-nginx-patch.md` (NOT via `kubectl apply` - see the comment in the file) for
  **2 replicas + the same anti-affinity** - this is the "clustered LB" from the original
  question.
- `k8s/07-ingress.yaml`: routes to the now-internal `studylife-web` ClusterIP service. Placeholder
  hostname `studylife.example.invalid` - adjust to the actual hostname before real deployment.
- **Core of the answer to the original question**: TLS certificates in Kubernetes are normal
  `Secret` objects, not bound to individual pods - all ingress-nginx replicas automatically see
  the same certificate. No additional per-replica configuration is needed, which is exactly the
  advantage of this approach over the previous hand-built nginx solution
  (`nginx-scale/nginx.conf`, which is still only intended for the local docker-compose test
  path).
- Verified locally (from inside the cluster, since the MetalLB IP on the Docker Desktop `kind`
  network isn't directly reachable from the Windows shell - a pure environment limitation of
  this local test setup, doesn't affect the real cluster): `curl` with a host header against the
  internal `ingress-nginx-controller` service returns `HTTP 200` over HTTPS (with the fallback
  certificate, since `studylife-tls` doesn't exist yet).
- **Two-host pattern** (internal via the home network + public via the external NPM reverse
  proxy, see "GitLab Integration"/NPM above): every resource intended to be public gets two
  ingress rules on the same service, one hostname for the home-network DNS (e.g.,
  `studylife.home.lan`) and one for the external domain (e.g.,
  `studylife.example.com`) - the external NPM proxy passes the host header used by the
  client through unchanged, and without the second rule this would fall through to the default
  backend (404). Already in place for `studylife-web` and Grafana; `k8s/09-uptime-kuma.yaml`
  (Uptime Kuma) previously did NOT have this second host (only a placeholder without
  `bootstrap-cluster.ps1` substitution logic) - added live afterward: new parameters
  `-UptimeKumaHost`/`-UptimeKumaPublicHost`.

### Rate Limiting (generous) + an important finding: ingress-nginx has been discontinued by the Kubernetes project

**Rate limiting** (`nginx.ingress.kubernetes.io/limit-rps: "20"` + `limit-burst-multiplier: "10"`)
on all four publicly reachable ingresses (studylife-web, Grafana, Uptime Kuma, piwatch) -
protects against obvious abuse (scanners/bots/brute-force) without disrupting normal usage: 20
requests/second per client IP as the sustained rate, but a burst up to 200 (`rps *
burst-multiplier`) allowed with `nodelay` - exactly the headroom that, for example, the Blazor
WASM start page needs when it loads several `.dll`/`.wasm` files in parallel. Verified live in the
generated `nginx.conf` (`limit_req zone=... burst=200 nodelay`), all four services remained
normally reachable afterward. **Important for
`07-ingress.yaml`/`17-grafana.yaml`/`09-uptime-kuma.yaml`**: these annotations were NOT set via
`kubectl apply -f` (that would again overwrite the placeholder hostnames, see the incident
above) - instead applied in a targeted way via `kubectl annotate` on the running ingress
resources, with the Git files separately updated to add the same annotations.

**No WAF/ModSecurity implemented - deliberately, for two reasons**: first, ModSecurity isn't even
compiled into this `registry.k8s.io/ingress-nginx/controller:v1.11.3` image anymore (`nginx -V`
shows no `modsecurity`) - ingress-nginx's own ModSecurity support was already removed some time
ago, in parallel with ModSecurity's own industry-wide end-of-life (Trustwave discontinued support
on July 1, 2024). Second, and considerably more important: **ingress-nginx itself has been
completely discontinued by the Kubernetes project (SIG Network/Security Response Committee)** -
the repository was archived (read-only) on March 24, 2026, and since then there have been NO
further releases, bugfixes, or security updates, not even for newly discovered CVEs. The cluster
has therefore been running for around four months (as of this doc update: July 2026) on a
component with no future prospect of patches at all. The project's official statement: "Existing
deployments of Ingress NGINX will not be broken" (nothing breaks acutely), but also: "If you are
not already using ingress-nginx, you should not be deploying it." The project's recommended
migration path: the [Gateway API](https://gateway-api.sigs.k8s.io/guides/) (a completely
different resource model - `Gateway`+`HTTPRoute` instead of `Ingress`, would require rebuilding
all four ingress manifests plus the cert-manager integration). **Deliberate decision**: given
this, no further complexity (ModSecurity/OWASP CRS, snippet annotations - currently locked out
anyway via `allow-snippet-annotations: "false"`) is invested into a component already declared
dead. The migration to the Gateway API needs to be planned as its own, larger effort, not on the
side - until then, ingress-nginx stays in use (still works technically), but with the clear
understanding that CVEs against it will no longer be fixed.

### Migration to NGINX Gateway Fabric (Gateway API) - complete

The "own, larger effort" announced in the previous section has been implemented AND is now fully
complete: [NGINX Gateway Fabric](https://docs.nginx.com/nginx-gateway-fabric/) (NGF, v2.6.7) is
the ONLY ingress path into this cluster; `ingress-nginx` has been completely uninstalled
(namespace, deployment, RBAC, IngressClass - all removed via `kubectl delete -f` against the same
manifest it was originally installed with). It has its own MetalLB IP (`192.168.1.241`) - the
rollout deliberately ran as a parallel setup (ingress-nginx kept `.240` until full verification
AND the external NPM switchover), which was the active fallback at the time of the migration
itself, but is now history. Installation steps including cert-manager activation
(`--enable-gateway-api`): `k8s/06c-nginx-gateway-fabric.md`; the resources themselves:
`k8s/07c-gateway.yaml` (NginxProxy + Gateway with 8 HTTPS listeners), `k8s/07d-httproutes.yaml`
(HTTPRoutes/BackendTLSPolicies/RateLimitPolicies for studylife-web, Uptime Kuma, Grafana; piwatch
analogously in the piwatch repo's `deploy/httproute.yaml`), parallel NetworkPolicy rules in
`k8s/12-network-policies.yaml`.

All the ingress building blocks got a direct Gateway API counterpart: `Ingress` rules →
`HTTPRoute` (with BOTH hostnames per service), `backend-protocol: HTTPS` → `BackendTLSPolicy` (an
important difference: the Gateway API mandatorily VERIFIES the backend hop against a CA -
referencing the `ca.crt` in the respective `*-backend-tls` secret plus the service DNS name as
the expected SAN, instead of ingress-nginx's `proxy-ssl-verify: off` default), `limit-rps`/
`limit-burst-multiplier` → a native `RateLimitPolicy` (20 r/s, burst 200, `noDelay`, 503 -
verified live via a curl `--parallel` burst: 288×200/112×503 out of 400 requests),
cert-manager annotation → the same annotation directly on the `Gateway` (creates its own
Certificates in the `nginx-gateway` namespace, the existing Ingress certificates remain
untouched). The WebSocket upgrade for piwatch's `/ws` (standard behavior with ingress-nginx, not
guaranteed with NGF) was explicitly verified against the new gateway IP (`HTTP/1.1 101 Switching
Protocols` via `curl --http1.1` with upgrade headers).

**The most important behavioral difference found live**: NGF correctly responds, per the spec,
with **421 Misdirected Request** to requests whose TLS SNI and host header belong to different
listeners - so the previous NPM pattern (SNI always `X.home.lan`, public host header passed
through) simply does NOT work with NGF, and can't be turned off either. Solution: TWO listeners
per service (home.lan + public domain, shared TLS secret, certificate covers both names), and NPM
has to be changed during the switchover to make the SNI follow the host header
(`proxy_ssl_server_name on; proxy_ssl_name $host;` in the proxy host's advanced config) - details
in `k8s/06c-nginx-gateway-fabric.md`. Both hostnames per service were verified with SNI=host
against the new IP (200/302 depending on the service), and the old ingress-nginx paths remained
reachable unchanged throughout the entire migration.

Monitoring: Prometheus job `nginx-gateway-fabric` (port 9113, control plane
`nginx_gateway_fabric_*`/`controller_runtime_*`, data plane `nginx_http_*` - metric names
verified live against `/metrics`, NOT taken from the docs); the old `ingress-nginx` job was
removed after decommissioning. The Grafana dashboard "ingress-nginx" (17b) was rewritten for NGF
metrics (uid `studylife-ingress` deliberately kept); note: the NGF data plane exports
`nginx_http_requests_total` under the same name as the NPM stub_status exporter, so the NPM
dashboard panel needed a `job="npm"` filter. The Grafana alert "No ingress-nginx replica online"
(`up{job="ingress-nginx"}`) would have fired permanently and incorrectly after removing the job -
switched to `up{job="nginx-gateway-fabric", component="studylife-gateway-nginx"}` (deliberately
only the data plane, not the control plane - whose failure doesn't immediately interrupt existing
connections).

**Closing steps, all carried out**: switched NPM to the gateway IP `.241` (including the SNI
advanced config above, confirmed live by the operator: public hostnames work), then completely
uninstalled ingress-nginx, deleted the 4 old `Ingress` objects (including the corresponding
manifest files/blocks in Git), removed the `allow-ingress-to-*` NetworkPolicy rules (the purely
internal allowances for Uptime Kuma stayed, where they're needed independently of the ingress
controller), updated the old Prometheus job and the outdated Grafana alert. Verified again
against all 4 services after each step (200/302 depending on the service) and checked
cluster-wide pod health.

### TLS Certificate: Switched from Cloudflare Origin CA to a Dedicated Internal cert-manager CA

**Original approach (now superseded)**: a Cloudflare Origin CA certificate (free, valid for
~15 years, generated manually in the Cloudflare dashboard, applied via `kubectl create secret tls
studylife-tls`/`grafana-tls`) for the hop between the external Nginx Proxy Manager (NPM) and
ingress-nginx. Only trusted by Cloudflare's edge, not by a browser that addressed the cluster
directly.

**Why it was superseded**: NPM itself already terminates TLS toward the internet with a real,
Cloudflare-issued certificate for the same domain (the hop to real clients) - a SECOND Cloudflare
Origin certificate for the internal NPM→ingress-nginx hop uses the same trust space twice, with
no real added value. Instead, there's now a DEDICATED internal certificate authority managed by
the cluster itself via [cert-manager](https://cert-manager.io/) - independent of Cloudflare,
automatic issuance/renewal instead of manual ~15-year files.

**Setup** (`k8s/07b-cert-manager-issuers.yaml`, the classic cert-manager "self-signed CA
bootstrap" pattern):
1. `ClusterIssuer/studylife-selfsigned-bootstrap` (type `selfSigned`) - a pure bootstrap issuer,
   only issues the root CA itself.
2. `Certificate/studylife-internal-ca` (`isCA: true`, namespace `cert-manager`) - generated with
   issuer 1, this is the actual internal root CA.
3. `ClusterIssuer/studylife-internal-ca-issuer` (type `ca`) - references the secret from step 2;
   ALL real leaf certificates are issued through this one.

`k8s/07-ingress.yaml` (studylife-web) and `k8s/17-grafana.yaml` (Grafana) now carry the annotation
`cert-manager.io/cluster-issuer: studylife-internal-ca-issuer` instead of a manually generated
secret - cert-manager generates/renews `studylife-tls`/`grafana-tls` from it automatically, the
secret name stays the same. Verified live: `kubectl get certificate -A` shows `READY: True` for
all three certificates (including the root CA itself), and an `openssl s_client` against the
ingress-nginx service shows `CN=studylife-internal-ca` as the issuer instead of the old
Cloudflare Origin certificate.

**Uptime Kuma deliberately NOT switched over** in this round (still uses the old Cloudflare
Origin certificate for the same hop) - simply not part of the original task, though the same
pattern (adding the annotation) would transfer identically.

**Important note for NPM** (outside this repo/cluster, not managed here): if NPM validates the
upstream certificate when forwarding to ingress-nginx, "Verify Origin Server Certificate" (or
equivalent) may need to be disabled there for the respective proxy host, since the internal CA is
unknown to NPM.

### Encrypting the NEXT Hop: ingress-nginx → Backend Pods

Previously, ingress-nginx terminated TLS and spoke plain HTTP internally to the backend pods -
now additionally encrypted, with leaf certificates from the same in-cluster CA
(`studylife-internal-ca-issuer`). Solved differently per backend, depending on what TLS options
the respective stack brings along:

- **studylife-web (Kestrel)**: a new `Certificate studylife-web-backend-tls` (DNS names = internal
  service DNS names, purely in-cluster, no hostname placeholder needed), mounted, Kestrel gets an
  ADDITIONAL HTTPS listener on port 8443 (`Program.cs`, `WebBackendTls:CertPath`/`KeyPath`, set
  only in K8s operation - the Pi/docker-compose.yml stays unchanged, plain HTTP:8080). Important
  Kestrel pitfall: `ConfigureKestrel(...).Listen(...)` OVERWRITES the endpoints derived from
  `ASPNETCORE_URLS` instead of adding to them - the existing port 8080 therefore has to be
  explicitly listed too, otherwise it would disappear without replacement (probes/the internal
  Uptime Kuma check still use plain HTTP on 8080). The `allow-ingress-to-web` NetworkPolicy was
  extended to cover port 8443 (additive to 8080). **Only actually becomes active after the next
  CI image build + Flux rollout** - the ingress annotation (`backend-protocol: HTTPS`) is present
  in the Git manifest, but was deliberately NOT applied live to the running ingress resource
  (tested live and immediately rolled back), because the currently running image doesn't yet
  contain the 8443 listener - switching over prematurely would have caused a 502 (ingress-nginx
  speaking TLS against a plain HTTP port). To be applied manually once a new image with this
  change has been rolled out.
- **Grafana**: `GF_SERVER_PROTOCOL=https` + a mounted certificate (`grafana-backend-tls`) - fully
  active live, Grafana ships native HTTPS support.
- **Uptime Kuma**: contrary to the original assumption, Uptime Kuma already natively supports its
  own TLS via `UPTIME_KUMA_SSL_KEY`/`UPTIME_KUMA_SSL_CERT` (no sidecar reverse proxy needed, see
  the [Uptime Kuma wiki](https://github.com/louislam/uptime-kuma/wiki/Environment-Variables)) -
  fully active live.

All three: `nginx.ingress.kubernetes.io/backend-protocol: "HTTPS"` annotation, `proxy-ssl-verify`
deliberately NOT set to `"on"` - the goal is encryption against eavesdropping on the cluster
network, not protection against an already-compromised pod (that would require its own internal
PKI with per-pod client certificates, disproportionate for this learning project).

**Bug found live and fixed**: the ingress annotations were once accidentally set via the FULL Git
file (with placeholder hostnames like `studylife.example.invalid`) instead of a targeted
applied - this briefly broke real reachability (404 due to a host mismatch, `studylife-tls`
briefly reissued for the wrong domain). Fixed immediately with the real hostnames, verified
several times returning 200/302. **Lesson**: for files with `bootstrap-cluster.ps1` placeholders
(`07-ingress.yaml`, the `17-grafana.yaml` base file, `09-uptime-kuma.yaml`), NEVER `kubectl
apply` the full file - instead apply only the changed field in a targeted way (`kubectl
annotate`/`kubectl patch`) - exactly as already documented for a different case in the
`bootstrap-cluster.ps1` comment on `06b-ingress-nginx-patch.md`.

**Second bug found and fixed live**: `uptime-kuma-tls` (the external certificate, not the backend
certificate just described) existed neither as a secret nor as a `Certificate` - the
`cert-manager.io/cluster-issuer` annotation was completely missing on the Uptime Kuma ingress
(unlike studylife-web/Grafana). ingress-nginx fell back to its unvalidated fake default
certificate for this host. Fixed live via `kubectl annotate` on the live resource (same reason:
the file itself still has placeholder hostnames), verified the certificate showed `Ready: True`
and reachability again.

**Third bug found live, this time in Uptime Kuma's own monitor configuration rather than in the
cluster**: the internal monitor "StudyLife Web (intern)" kept checking `http://studylife-web...`
(plaintext) - since the Kestrel HTTPS termination above became active, this path now responds
with `307` to `https://...:8443` (ASP.NET's `UseHttpsRedirection` previously had no valid HTTPS
port and therefore didn't redirect at all, now it does so correctly) - a plain HTTP monitor
treats `307` as "down". After switching the monitor URL to `https://`, the monitor STILL showed
down: `curl` (without `-k`) confirmed `SSL certificate ... unable to get local issuer
certificate` - our internal `studylife-internal-ca` is naturally not known to the default trust
store. Fix: enable the **"Ignore TLS/SSL error for HTTPS websites"** option in the monitor itself
(Uptime Kuma's equivalent of `curl -k`) - sufficient for a purely internal cluster check with a
self-controlled CA, no need to set up trusting the CA globally in Uptime Kuma
(`NODE_EXTRA_CA_CERTS`).

## Postgres Backups to Cloudflare R2

CNPG supports WAL archiving + base backups against any S3-compatible endpoint via
`spec.backup.barmanObjectStore` - [Cloudflare R2](https://www.cloudflare.com/products/r2/) is
S3-compatible and fits directly. In addition to the replica redundancy from the CNPG section
above, this also protects against data corruption/accidental `DELETE` (replicas only protect
against node failure - a faulty write replicates immediately to all replicas too) and enables
point-in-time recovery instead of just "failover to a replica".

**Manual steps (only possible in the Cloudflare dashboard/account)**:
1. Create an R2 bucket (ours is `studylifebackup`).
2. Generate an R2 API token with write permissions (Dashboard → R2 → "Manage API Tokens") →
   provides an access key ID + secret access key.
3. Note the account ID (part of the endpoint URL
   `https://<ACCOUNT_ID>.r2.cloudflarestorage.com`).
4. `kubectl create secret generic r2-backup-credentials -n studylife-scale
   --from-literal=ACCESS_KEY_ID=... --from-literal=ACCESS_SECRET_KEY=...` (not in the repo).

Then fill in the `spec.backup` block in `k8s/02-postgres.yaml` with the real bucket name/account
ID and apply `k8s/08-scheduled-backup.yaml` (a daily base backup at 03:00, in addition to
continuous WAL archiving). `retentionPolicy: "7d"` keeps storage usage bounded - CNPG's own
operator automatically deletes backups/WALs beyond this window, no additional CronJob needed.

**Is now active on prod** (`spec.backup` in `k8s/02-postgres.yaml` no longer commented out,
secret + bucket exist). Ran through it live and found two real pitfalls in the process:

- **`destinationPath`/bucket name must match exactly** - a typo here (the bucket was named
  `studylife-pg-backups` in the manifest, but actually created in R2 as `studylifebackup`)
  causes a `403 Forbidden` on every `HeadBucket` call from `barman-cloud-*`, even though the
  credentials themselves are correct - easy to mistake for a real credential/permissions
  problem. Diagnosis: the CNPG instance manager's pod logs (`kubectl logs <primary-pod> -c
  postgres`) show the barman-cloud error message in plain text; a direct test with `aws s3api
  head-bucket --endpoint-url ...` (from a debug pod with the real secret values as env vars)
  reproduces it independently of CNPG.
- **Restore can fail with `WAL ends before end of online backup` when there's barely any write
  load**: without `archive_timeout` configuration, a WAL segment is only archived once it's full
  (16MB) - on a small test/learning database with little traffic, the segment marking the end of
  the backup can stay open for days. A restore right after a backup then fails, because barman
  can't find the needed WAL segment. Workaround: run `SELECT pg_switch_wal();` once on the
  primary before the restore (forces archiving of the open segment), then retry. In production
  with real traffic this practically never happens, since segments rotate regularly there due to
  genuine write load.

**Restore tested live** (a temporary CNPG cluster in its own namespace, `bootstrap.recovery` +
`externalClusters` with the same `barmanObjectStore`, `serverName` explicitly set to the source
cluster's name): the cluster reached "Cluster in healthy state", and real `COUNT(*)` queries
(not `pg_stat_user_tables.n_live_tup` - that's just a statistics estimate and incorrectly shows 0
after a restore until `ANALYZE` runs) confirmed an exact match with the original database. The
test namespace was then completely deleted.

## Resource Governance (Requests/Limits, PodDisruptionBudgets, Redis Limits)

No container had resource requests/limits before this round - the scheduler couldn't meaningfully
bin-pack, and a runaway pod (e.g., GC pressure under load) could have starved neighbors on the
same Pi. Now `studylife-web`/`-worker`, `redis-cluster`, the CNPG instances (`spec.resources` in
the cluster manifest), and `uptime-kuma` all have explicit `requests`/`limits`, each with the
limit as a rough 2x buffer over observed demand. CNPG additionally gets
`postgresql.parameters.shared_buffers: 128MB` (the standard rule of thumb of ~25% of the
instance limit, instead of the untuned default calibrated for a "normal" server). NGINX Gateway
Fabric's data plane (`studylife-gateway-nginx`) brings its own `requests`/`limits` (50m/90Mi
requests, 200m/200Mi limits) - part of the NGF installation itself, see "Migration to NGINX
Gateway Fabric" above.

**Found live during the NGF dashboard research (2026-07-18): Prometheus OOMKilled.** `kubectl -n
monitoring describe pod -l app=prometheus` showed `Last State: Terminated, Reason: OOMKilled` -
actual usage was 483Mi against the old limit of just 512Mi (`requests: 256Mi`). Cause: many
scrape targets/dashboards (Node Exporter Full with 238 panels, NGF controller metrics with ~30
controllers, CoreDNS/MetalLB/Flux/CloudNativePG) push time-series cardinality well above the
originally calibrated budget. Fix: raised `requests`/`limits` in `k8s/14-prometheus.yaml` to
`384Mi`/`1Gi` (node `pinode01` had enough buffer: 1760Mi of 4048Mi allocatable requested, 43%) -
no retention/cardinality cut needed, purely a resource-sizing catch-up.

**Gap found live, fixed live**: `studylife-pg-pooler` (PgBouncer, see "Connection Pooler" below)
was the only workload in the entire cluster without `resources` -
`k8s/11-pooler.yaml`'s `spec.template.spec.containers[0].resources` (container name `pgbouncer`)
is now set analogously to Redis (if anything even lighter, a pure proxy with no data store of
its own).

### SecurityContext, Token Automount, Pod Security Standards

**SecurityContext**: `studylife-web`/`-worker` get `runAsNonRoot: true` +
`seccompProfile: RuntimeDefault` (pod level) as well as `allowPrivilegeEscalation: false` +
`capabilities.drop: [ALL]` (container level) - takes effect without `runAsUser`, because the
Dockerfile already sets `USER app` (a non-root user built into the `aspnet` base image); this is
only additionally enforced so that an accidental future removal of the Dockerfile's `USER`
statement would immediately be caught at deploy time instead of silently falling back to root.
piwatch only gets the image-independent parts (see "Deliberate Scope Cuts" below for the reason
why no `runAsNonRoot` there).

**Token automount**: `automountServiceAccountToken: false` for all workloads with no genuine
Kubernetes API need (`studylife-web`, `redis-cluster`, Grafana, Uptime Kuma,
piwatch-node-agent) - defense in depth: a compromised pod without an API token can't be misused
against the Kubernetes API itself. `studylife-worker` (needs the worker API for
`KubernetesWorkerReplicaCountProvider`), Prometheus, kube-state-metrics, and piwatch's main
deployment (ClusterRole for the watch API) keep their token because they genuinely need it.

**Pod Security Standards**: namespace labels instead of an additional admission webhook (built
into Kubernetes since 1.25, no extra controller needed) - details and the rationale for the
different strictness between `studylife-scale` (`enforce: baseline`) and `monitoring`
(only `warn`/`audit: baseline`, due to piwatch-node-agent's/node-exporter's legitimate hostPath
need) are under "Deliberate Scope Cuts" below.

Redis (`k8s/03-redis.yaml`) now has `maxmemory 96mb` + `maxmemory-policy allkeys-lru` - as a pure
cache with no mandatory persistent data, eviction under memory pressure is unproblematic.

`k8s/10-pod-disruption-budgets.yaml` prevents a voluntary node drain/cluster upgrade from taking
down ALL replicas of a deployment at once (`studylife-web minAvailable: 1`, `studylife-worker
minAvailable: 1`, `redis-cluster minAvailable: 5`, `studylife-gateway-nginx minAvailable: 1`) -
a direct complement to the anti-affinity above. CNPG already automatically manages a PDB for its
own cluster pods, no manual one needed. **Found live during the ingress-nginx decommissioning**:
NGINX Gateway Fabric does NOT create its own PDB for its data plane (contrary to what was
assumed) - `studylife-gateway-nginx` would have been unprotected during a node drain until the
PDB here was added.

## Connection Pooler (PgBouncer via CNPG Pooler)

`k8s/11-pooler.yaml` (a CNPG `Pooler` resource, `type: rw`, `pgbouncer.poolMode: transaction`,
2 instances for its own redundancy) - avoids every pod opening its own Npgsql connection pool
against Postgres' `max_connections` as the number of Web replicas grows. CNPG automatically
creates a service with the same name as the pooler object (verified live: **`studylife-pg-pooler`,
no `-rw` suffix** - unlike the cluster service itself). `k8s/dev/01-secrets.yaml`'s (formerly
`k8s/01-config-and-secret.yaml`'s, see "Prod Secret vs. Learning-Cluster Placeholder" above)
`Database__ConnectionString` now points to this pooler instead of directly to
`studylife-pg-rw`, with `Max Auto Prepare=0` (MANDATORY in transaction pooling mode - PgBouncer
doesn't hold a fixed server connection per client there, and Npgsql's default behavior of
server-side cached prepared statements would otherwise cause "prepared statement already exists"
errors as soon as PgBouncer moves the same client to a different server connection). Verified
empirically with the complete `ApiVerify` script (registration, read/write, cache consistency,
full-text search) - all green, no prepared-statement errors. Rollback path if problems arise:
point the `Database__ConnectionString` host back at `studylife-pg-rw`.

Implementation detail: since the current Postgres password can't/shouldn't be read out (see the
security guardrail below), it was rotated for this change - via a temporary pod that uses the
OLD password exclusively internally via Kubernetes `secretKeyRef` injection (never seen by me),
running `ALTER ROLE` with a newly generated password, after which both secrets
(`studylife-secrets`, `studylife-pg-app-secret`) were updated consistently.

## NetworkPolicies

`k8s/12-network-policies.yaml`: by default in Kubernetes, any pod can reach any other pod in the
same cluster - now `studylife-scale` has a default-deny ingress with targeted exceptions (NGINX
Gateway Fabric → Web, Web/Worker → Postgres/Pooler/Redis, the Redis cluster bus among itself, all
pods → kube-dns, the `monitoring` namespace → the metrics ports of CNPG/redis_exporter).
Originally the ingress source was `ingress-nginx` - after it was replaced by NGINX Gateway Fabric
(see "Migration to NGINX Gateway Fabric" above), the rules are now named
`allow-nginx-gateway-to-*` with source namespace `nginx-gateway`.

**Verified empirically with both a positive AND a negative test** (not just assumed): `ApiVerify`
stayed fully green after applying (legitimate traffic keeps working), AND a deliberately
unlabeled test pod without the allowed labels could reach neither Postgres nor Redis (`nc`
connection attempts ran into a timeout) - so Docker Desktop's Kubernetes CNI does genuinely
enforce NetworkPolicies (previously unclear, now confirmed).

**Additionally confirmed empirically on the real Pi cluster** (not just assumed on Docker
Desktop): a dedicated test namespace with two pods, `curl` before a default-deny policy (`200`),
afterward (connection fails/times out) - K3s' built-in network-policy controller (no separate
Calico/Cilium needed) also genuinely enforces NetworkPolicies on the real two-Pi cluster, not
just in theory/per the docs.

**Bug found live (a second one, independent of the CNPG operator finding below)**: `k8s/09-
uptime-kuma.yaml`'s own ingress never got a matching NetworkPolicy allowance - the default-deny
had completely blocked Uptime Kuma since the very first apply of this file, unnoticed because
`kubectl get pods` kept showing `Running` (the NetworkPolicy only blocks incoming traffic; the
pod itself ran fine). Confirmed via a debug pod from the `ingress-nginx` namespace (before:
connection failed; after: `HTTP 302` to `/dashboard`). Fix: added an
`allow-ingress-to-uptime-kuma` rule, analogous to `allow-ingress-to-web`.

**Bug found live in the process**: the default-deny also blocked the CNPG OPERATOR itself (runs
in the separate `cnpg-system` namespace) - `kubectl get cluster` incorrectly showed "Instance
Status Extraction Error" afterward, even though Postgres itself kept running fine (the operator
could just no longer query each instance's status over its port 8000, found via `i/o timeout`
errors in the operator logs). Fix: a dedicated `allow-cnpg-operator-to-instances` rule that
explicitly allows `cnpg-system` → port 8000. A clear warning for any future NetworkPolicy
tightening in this repo: don't just think about your own app pods, but also about
operators/controllers in FOREIGN namespaces that need access to your pods.

### `monitoring` Namespace Retrofitted (was completely unprotected)

Until then, no NetworkPolicy applied to `monitoring` (Grafana, Prometheus, kube-state-metrics,
node-exporter, piwatch) at all - in particular piwatch (Kubernetes live dashboard, see its own
section below) has cluster-wide read access to pods/nodes/events/logs via a ClusterRole AND is
reachable via two external hostnames. A compromised piwatch pod would therefore have had
unrestricted network access to the entire cluster network. Now the same default-deny-ingress
strategy as `studylife-scale`, with targeted exceptions (NGINX Gateway Fabric →
Grafana/piwatch, Prometheus → itself/Grafana/kube-state-metrics/node-exporter, piwatch →
piwatch-node-agent, Uptime Kuma (from `studylife-scale`) → Grafana/piwatch, analogous to the
existing `allow-nginx-gateway-to-web` pattern). Egress deliberately stays open like in
`studylife-scale` - Prometheus' platform-wide scraping (CoreDNS, MetalLB, Flux, NGINX Gateway
Fabric, cert-manager, CNPG/Redis in `studylife-scale`) would otherwise need a complete egress
whitelist per scrape job.

**Own bug found live while writing this policy**: piwatch's container port was initially entered
incorrectly as 8080 (a mix-up while reading several `kubectl` outputs in the same order as
Grafana/Prometheus/kube-state-metrics) - piwatch actually listens on port 8000. Caught
immediately via an HTTP 502 test over the real ingress path, corrected, verified again.

**Independent bug found live while verifying**: the `grafana` scrape job in
`k8s/14-prometheus.yaml` had no `scheme: https`, even though Grafana enforces
`GF_SERVER_PROTOCOL=https` (port 3000 is TLS-only) - the target had therefore shown permanently
`down` since the TLS switchover (`server returned HTTP status 400 Bad Request`, Go's HTTP server
response to plaintext HTTP against a TLS listener). Fixed analogously to the existing `cnpg` job
(`scheme: https` + `insecure_skip_verify: true`).

**node-exporter (hostNetwork:true, hostPID:true)**: the ingress rule for it doesn't hurt, but may
have no practical effect - whether the respective CNI enforces NetworkPolicies for
hostNetwork pods at all is implementation-dependent, because the traffic never passes through the
normal pod network bridge.

## Observability: Self-Hosted Prometheus + Grafana + Loki

**Cloudflare has no suitable free option for this purpose** (checked): Cloudflare Web Analytics
is pure frontend page-view analytics (no infrastructure/pod/DB metrics relevance), and Logpush
for your own origin/app logs is tied to higher Cloudflare plans. So this is entirely self-hosted
(no external account needed), a new `monitoring` namespace:

- **[Uptime Kuma](https://github.com/louislam/uptime-kuma)** (`k8s/09-uptime-kuma.yaml`,
  already live from an earlier round, version v2 as of this round - see below). Pure HTTP
  health-check monitoring + push/email/webhook notification on failure - "know immediately when
  something is broken". Has its own ingress, so it stays reachable even if StudyLife itself is
  down.

  **Updated v1 → v2**: not a deliberate pin to the old version, just a never-revisited default
  tag (`:1` instead of `:2`). Tested locally (real v1 data with 3 existing monitors) before
  rolling out to prod: the automatic migration completed cleanly on every start ("Aggregate
  Table Migration Completed"), all monitors were preserved. Additionally set
  `UPTIME_KUMA_SQLITE_SINGLE_CONNECTION=true` - recommended by the upstream wiki for Raspberry Pi
  operation due to possible SQLite locking issues (our cluster runs on real Pis). Known breaking
  change v1→v2: sorting on status pages removed (not used by us).
- **Prometheus** (`k8s/14-prometheus.yaml`) - scrape targets selected via existing pod labels
  (`relabel_configs` with `keep`), NOT via new annotations, so the Web/Worker/CNPG/ingress-nginx
  manifests didn't have to be touched for this: CNPG (port 9187, the operator ships the metrics
  exporter automatically), `redis_exporter` sidecars (port 9121, new in `k8s/03-redis.yaml`, one
  exporter per Redis pod), ingress-nginx (port 10254 - not enabled by default in the official
  cloud deploy, turned on afterward via `--enable-metrics=true`, see
  `k8s/06b-ingress-nginx-patch.md`), kube-state-metrics, node-exporter. 7 days retention
  (`--storage.tsdb.retention.time`), keeps growth within the Pi storage budget.

  **Five additional scrape jobs added afterward** (a full inventory of all running prod
  processes, each metrics port verified live via a debug pod before the job was added): CoreDNS
  (port 9153), MetalLB controller+speaker (port 7472), all 4 Flux controllers (port 8080),
  Prometheus/Grafana itself. **Label collision found+fixed**: the own pod-identification label
  was initially called `controller`, which would have collided with Flux's OWN `controller` label
  on `controller_runtime_*` metrics (Prometheus would have renamed it to `exported_controller`,
  confusing) - now `flux_controller`. **Not added**: CNPG pooler metrics (port 9127 declared in
  the pod spec, but "connection refused" - the PgBouncer metrics exporter apparently isn't active
  for us, a topic of its own, see "Deliberate Scope Cuts").

  **NPM/external reverse proxy** (`static_configs`, no `kubernetes_sd_configs`, since NPM +
  Keepalived run outside the K8s cluster directly as a Docker Compose stack on both Pis, with the
  setup script for that deliberately outside this repo): `nginx-prometheus-exporter` scrapes
  NPM's `stub_status`, exposed via the official NPM custom-config injection point
  (`data/nginx/custom/http.conf`, internal port 9911, NOT publicly published - only reachable
  from the exporter container in the same Docker network). Only basic metrics available (active/
  reading/writing/waiting connections, total requests) - `stub_status` provides no latency/
  status-code breakdown.

  **ingress-nginx metrics limitation found live**: despite `--enable-metrics=true` AND real test
  traffic, this version exports NO `nginx_ingress_controller_requests`/
  `_request_duration_seconds_bucket` (consistent with several known, open GitHub issues in the
  ingress-nginx project) - the affected dashboard panels instead use the reliably present
  `nginx_ingress_controller_nginx_process_*` metrics (coarser, without status-code/latency
  breakdown).
- **node-exporter** (`k8s/15-node-exporter.yaml`, DaemonSet) + **kube-state-metrics**
  (`k8s/16-kube-state-metrics.yaml`) - host-level and K8s-object-level data for the cluster
  overview dashboard.
- **Grafana** (`k8s/17-grafana.yaml` for the infrastructure, dashboard contents split into
  `k8s/17b`/`17c`, see below) - datasource AND all dashboards fully provisioned via ConfigMap
  (no manual setup needed). Fixed datasource UIDs (`prometheus`/`loki` instead of the otherwise
  randomly generated per-Grafana-instance ones) - needed because several community dashboards
  reference datasources by a fixed UID; a random UID wouldn't be the same between the test
  cluster and prod anyway.

  **Language note**: the custom dashboard/alert titles and descriptions in `k8s/17b-grafana-dashboards.yaml`
  and `k8s/17d-grafana-alerting.yaml` are in German - this is operator-facing monitoring tooling
  for this specific deployment, not a core product surface, so it wasn't translated along with
  the rest of the codebase. Adapt or translate freely for your own deployment.

  **Dashboard architecture**: ONE ConfigMap for all "core" dashboards (`grafana-dashboard-core`)
  and a second one for community dashboards (`grafana-dashboard-community`), each mounted as a
  WHOLE directory (no `subPath`) under `/etc/grafana/provisioning/dashboard-json/{core,community}`
  - a new/changed dashboard therefore only needs a new key in `k8s/17b`/`17c`, NEVER AGAIN a
  change to the Grafana deployment itself (previously: one ConfigMap + one `subPath` mount PER
  dashboard). Two dashboard providers/folders (`StudyLife` / `StudyLife (Community)`).

  **8 core dashboards** (`k8s/17b-grafana-dashboards.yaml`, all panel queries written against
  metric names actually observed live, not blindly imported):
  1. **Overview** - a traffic-light view (stat panels with color thresholds instead of time
     series/raw numbers): app/DB/Redis/ingress online?, CPU/memory as gauges, last backup,
     response times.
  2. **CloudNativePG** - replication lag, connections, WAL rate, last backup.
  3. **Cluster overview** - CPU/memory per node (as a percentage share, not raw bytes - directly
     comparable across differently sized nodes), pod restarts, pod distribution across nodes.
  4. **Redis Cluster** - memory, hit/miss ratio, evictions, replication offset master/replica.
  5. **NGINX Gateway Fabric** (`uid: studylife-ingress`, renamed from "ingress-nginx" during the
     migration, see "Migration to NGINX Gateway Fabric" above) - requests/second per data-plane
     pod, connection states, control plane online, processed config event batches/s,
     accepted-vs-handled (see above regarding the metrics limitation). Later extended with two
     control-plane panels ("find a dashboard with deeper insight" - researched, but neither the
     official NGF repo (`nginx/nginx-gateway-fabric`, a full file-tree search via the GitHub API,
     no match) nor grafana.com has a community dashboard with more depth than our own; the
     official sample dashboard (`docs.nginx.com/ngf/grafana-dashboard.json`) is even SHALLOWER
     than ours - so instead, two real additional panels were added, verified live against
     Prometheus: **reconcile errors per controller**
     (`controller_runtime_reconcile_total{result="error"}`, ~30 controllers, one per Gateway
     API/policy resource type) and **workqueue depth per controller** (`workqueue_depth`, a
     growing backlog = an overloaded/stuck controller). A native reload success/failure signal
     like ingress-nginx's (`nginx_ingress_controller_config_last_reload_successful`) still
     doesn't exist for NGF (checked again live against the complete metric list) - the two new
     panels are the next-best substitute for it.
  6. **Worker shard assignment** - the trick without any app code change: `redis_exporter`'s
     `--check-keys=worker:shard:*` exports the existing `RedisWorkerShardClaim` keys directly as
     a metric. Verified live: the panel shows exactly 3 claimed shards with 3 different instance
     IDs - proof that partitioning actually works.
  7. **Nginx Proxy Manager (external)** - active connections per Pi, requests/second,
     accepted-vs-handled (a persistent gap would indicate dropped connections).
  8. **Monitoring stack** - Prometheus/Grafana self-monitoring (scrape targets down, active time
     series, WAL size, Flux reconcile errors per controller) - built by hand, because the
     official community dashboard for this (ID 3662) uses outdated `graph`/`singlestat` panel
     types (Grafana 4/5 era, risky without a chance to test it live against Grafana 11).

  **7 community dashboards** (`k8s/17c-grafana-community-dashboards.yaml`, "depth" for anyone
  wanting more technical detail - each individually verified live against real cluster data, none
  adopted blindly):
  - **Node Exporter Full** (grafana.com ID 1860) - 238 of 284 panel queries return real data on
    the virtualized test cluster; the rest are hardware sensors (temperature, CPU frequency
    scaling, pressure stall info) that are missing there but presumably work on the real Pi
    hardware.
  - **Redis** (ID 763, from the `redis_exporter` project itself) - the `$namespace` template
    variable removed (our exporter doesn't set such a label).
  - **NPM/nginx** (official, from the `nginx-prometheus-exporter` project) - only the usual
    datasource fix was needed (see below).
  - **CoreDNS** (ID 14981), **MetalLB** (ID 20162) - likewise only the usual datasource fix.
  - **Flux** (ID 16714) - **actually needed a real fix, not just the datasource fix**: Flux
    2.9.x completely removed `gotk_reconcile_condition`/`gotk_suspend_status` (a breaking change
    relative to the dashboard's version) - the 4 affected ready/failing panels were replaced
    with `controller_runtime_reconcile_total{result=...}` (the best available substitute), and
    the 2 readiness tables were removed rather than left broken, since there was no clean
    replacement. `exported_namespace` → renamed to `namespace` (the label name changed).
  - **CloudNativePG** (ID 20417, official, from the `cloudnativepg` project itself) - uses the
    same `cnpg_*` metric names as our already-running exporter (no mismatch like with generic
    `postgres_exporter` dashboards using the `pg_*` prefix). `${DS_PROMETHEUS}` → fixed UID
    `prometheus`, `${DS_EXPRESSION}` → the reserved expression pseudo-datasource UID `__expr__`.
    The `cnpg` scrape job (`k8s/14-prometheus.yaml`) additionally had to get a `namespace` label
    added (`__meta_kubernetes_namespace` → `namespace`) - the dashboard's own "Database
    Namespace" variable extracts this label directly from `cnpg_collector_up`, and the label
    simply didn't exist on our time series before. The "Operator Namespace" variable stays empty
    (needs `controller_runtime_webhook_requests_total` from `cnpg-system`, which we don't
    scrape) - the operator health panels depending on it show "No data", but the actual cluster
    panels (replication lag, WAL, backups, database sizes) are independent of that.
    **Systemic problem found live, not just with this dashboard**: of 107 panels with queries,
    ~14 (CPU/memory/volume usage, including the prominent "CPU Utilisation"/"Memory Utilisation"
    overview panels) depended on kubelet/cAdvisor metrics (`kubelet_volume_stats_*`,
    `container_cpu_usage_seconds_total`, `container_memory_working_set_bytes`) AND a standard
    Prometheus recording rule
    (`node_namespace_pod_container:container_cpu_usage_seconds_total:sum_irate`) that a full
    kube-prometheus-stack setup brings along automatically, but our lean, hand-built Prometheus
    didn't have - hence "many NoData cards" on first opening. Retrofitted (see
    "Kubelet/cAdvisor scraping" below), and now these panels return data too.
    **Second finding, after kubelet/cAdvisor alone wasn't enough**: all 4 query variables
    (`namespace`/`cluster`/`instances`/`operatorNamespace`) had `refresh: 2` ("only on time-range
    change") instead of `refresh: 1` ("on dashboard open") - on a freshly opened dashboard/tab,
    they could therefore stay empty, which made practically every panel query with
    `namespace="$namespace"` return nothing, even though the same queries with the real values
    returned data without issue directly against Prometheus (verified via a systematic script
    covering all 118 panel queries: 97 return data, 21 are expected gaps - operator-namespace
    panels + WAL/tablespace-volume panels, see below - no unexplained failures). Switched to
    `refresh: 1`. Also observed (not fixed, cosmetic): Grafana's alerting subsystem, while
    rendering the "Alerts" panel, also tries in the background to query the external ruler API
    of the **Loki** datasource entry (`loki.monitoring.svc.cluster.local` - a DNS error, since
    Loki isn't running for us, see "Open Item" above) - produces a 502 in the Grafana log, but
    doesn't affect the actual CNPG panel data.
    - **9 of the 21 expected gaps**: separate WAL/tablespace volumes (`persistentvolumeclaim
      =~"...-wal"`/`"...-tbs.*"`) - an optional CNPG feature (`walStorage`/`tablespaces`) that we
      don't use (one PVC per instance is enough for this learning setup).
    - **12 of the 21**: operator-namespace-dependent, see above.

  **General lesson, applies to EVERY grafana.com dashboard imported in the future**: placeholders
  like `${DS_PROMETHEUS}`/`${DS_PROM}`/`${ds_prometheus}` (spelling varies by dashboard author)
  are normally only resolved in Grafana UI's interactive "import dashboard" wizard - with pure
  file provisioning (our approach), they stay unresolved and every panel query runs against a
  non-existent datasource ("N/A" on every panel, noticed exactly this way live on prod). Fix:
  always replace with the fixed `prometheus` UID.
- **Loki** (`k8s/18-loki.yaml`, single-binary mode, filesystem storage, 7 days retention) +
  **Promtail** (`k8s/19-promtail.yaml`, DaemonSet) for app/pod logs, as a complement to the
  metrics, with no dedicated app-level tracing in the .NET code.

**Open item, stated honestly rather than glossed over**: Loki itself runs healthy and is wired up
as a Grafana datasource, but Promtail's Kubernetes pod discovery currently reports `0/0 active
targets` despite correctly confirmed RBAC (`kubectl auth can-i list/watch pods` returns `yes` for
the `promtail` ServiceAccount) and a `kubernetes_sd_configs` job definition that the `/config`
endpoint shows as correctly parsed - tested with Promtail 3.2.1 AND 2.9.10, same result. The
cause wasn't conclusively found (no error in the log, connection to the API server reported
successful in the log). Logs are therefore currently NOT searchable in Grafana; metrics/
dashboards are unaffected by this. Next debugging step when there's time: reproduce Promtail's
`kubernetes_sd_configs` against a simpler, isolated test instance (outside this cluster) to check
whether it's related to the unusually new Kubernetes version (v1.36.1 in Docker Desktop).

**Access**: Grafana/Uptime Kuma via the respective ingress hosts (placeholder, adjust) plus a TLS
certificate once available - until then, e.g., `kubectl -n monitoring port-forward
svc/grafana <port>:80` (default login `admin`/`admin`, prompted to change on first login).

### Kubelet/cAdvisor Scraping (was completely missing, noticed live during the CNPG dashboard check)

A full kube-prometheus-stack setup automatically scrapes every kubelet (`/metrics`, among other
things for `kubelet_volume_stats_*` - PVC storage usage) and its built-in cAdvisor
(`/metrics/cadvisor`, for `container_cpu_usage_seconds_total`/
`container_memory_working_set_bytes` - pod resource usage), plus a handful of standard recording
rules from the `kubernetes-mixin` project. Our hand-assembled Prometheus (no operator, no Helm
charts) had none of this - only noticed when the new CNPG community dashboard showed row after
row of "No data" on the CPU/memory/volume panels.

**Scraping via the API server proxy** (`k8s/14-prometheus.yaml`, two new jobs `kubelet` +
`cadvisor`) instead of direct network access to kubelet port 10250 - Prometheus only needs one
additional RBAC permission for this (`nodes/proxy`, `get`) against the already-reachable
Kubernetes API, no new network path/NetworkPolicy change needed.
`bearer_token_file: /var/run/secrets/kubernetes.io/serviceaccount/token` (the ServiceAccount
token already mounted anyway) authenticates against the kubelet. The `cadvisor` job explicitly
overrides its `job`/`metrics_path` labels to `kubelet`/`/metrics/cadvisor` (the
kube-prometheus-stack convention) - community dashboards typically filter on exactly that.

**Missing recording rule added**:
`node_namespace_pod_container:container_cpu_usage_seconds_total:sum_irate` (a new
`recording-rules.yml` ConfigMap file, referenced via `rule_files` in `prometheus.yml` - the same
ConfigMap is mounted as a whole directory, so a new key is enough). Deliberately simplified
compared to the kubernetes-mixin original (no `group_left(node)` join against `kube_pod_info` for
a "cluster" label - unnecessary for a single-cluster setup).

Verified live: `kubelet_volume_stats_available_bytes` shows real PVC fill levels (e.g.,
`studylife-pg-1`), `container_cpu_usage_seconds_total`/`container_memory_working_set_bytes`
return values for all `studylife-scale` pods, and the full CPU utilization panel query (ratio
against `kube_pod_container_resource_requests`) computes correctly.

## Alerting: Grafana-Native Alerting Instead of a Separate Alertmanager

No additional cluster component needed - Grafana is already there, and file provisioning for
alert rules works exactly like for the dashboards (`k8s/17d-grafana-alerting.yaml`, within the
Flux auto-deploy scope, no placeholders). 15 rules, all based on metrics already verified live
from the existing dashboards:

1. Web app unreachable (`kube_deployment_status_replicas_available` for `studylife-web`).
2. Worker not active (the same metric for `studylife-worker`).
3. Database cluster degraded (fewer than 2 of 3 CNPG instances online).
4. Redis cluster degraded (fewer than 5 of 6 nodes online).
5. No NGINX Gateway Fabric data plane online (originally for ingress-nginx, switched to
   `up{job="nginx-gateway-fabric",component="studylife-gateway-nginx"}` after its
   decommissioning - see "Migration to NGINX Gateway Fabric" above).
6. NPM down on at least one Pi.
7. Persistent Flux reconcile errors (`controller_runtime_reconcile_total{result="error"}`).
8. Certificate expires in less than 7 days
   (`certmanager_certificate_expiration_timestamp_seconds`, a new `cert-manager` scrape job in
   `k8s/14-prometheus.yaml`, only the main controller pod - `component=controller` - exports this
   metric, cainjector/webhook do not). 7 days instead of fewer, because cert-manager itself
   already renews 30 days before expiry (default `renewBefore`) - under 7 days remaining means
   the automatic renewal is long overdue and hasn't worked (yet). Without this alert, a silent
   expiry would make several services (studylife-web, Grafana, Uptime Kuma, piwatch - all via
   the same internal CA) unreachable at once before anyone noticed.
9. Postgres replication lag too high (`cnpg_pg_replication_lag > 60s`) - risks data loss to that
   extent in the event of a failover.
10. Postgres WAL archiving is currently failing
    (`cnpg_pg_stat_archiver_seconds_since_last_failure < 300`).
11. Postgres transaction ID wraparound risk (`cnpg_pg_database_xid_age > 150 million` - the
    `autovacuum_freeze_max_age` default is 200 million).
12. Postgres needs manual intervention (`cnpg_collector_manual_switchover_required` or
    `cnpg_collector_fencing_on`).
13. Redis memory close to `maxmemory` (>85% of 96MB - surface eviction pressure before the cache
    hit rate noticeably drops).
14. Low disk space on at least one Pi (`node_filesystem_avail_bytes` < 15% of root filesystem
    size).
15. piwatch unreachable (the same `kube_deployment_status_replicas_available` logic as Web/
    Worker).

**Deliberately NOT added**: a "no successful backup for X hours" alert.
`cnpg_collector_last_available_backup_timestamp` is permanently `0` for us (presumably a
limitation of the classic `barmanObjectStore` path, see the Barman Cloud deprecation note above,
not of the newer plugin backup path), and `cnpg_collector_last_failed_backup_timestamp` shows a
long-superseded one-time failure (~16h old at the time of checking), even though the actual
`ScheduledBackup` has, per `kubectl get backup`, completed cleanly again long since - an alert on
this metric would fire incorrectly and permanently from day 1. Until a more reliable metric is
available, backup success remains a manual/quarterly check (see "Postgres Backups to Cloudflare
R2").

### The Telegram Contact Point Is Deliberately NOT File-Provisioned - Two Grafana Bugs Found Live

**Bug 1**: Grafana's own `$VARIABLE` substitution for secrets in provisioning YAML (intended to
keep the bot token out of Git) incorrectly converts purely numeric values - Telegram chat IDs are
ALWAYS numeric - into a number during the internal JSON roundtrip, even though the YAML field is
quoted (`"cannot unmarshal number into Go struct field Config.chatid of type string"`).
Reproduced multiple times (also with realistic multi-digit IDs, not just `"0"`) - a broken
contact point makes Grafana crash IMMEDIATELY on startup (CrashLoopBackOff), so this isn't a
cosmetic problem.

**Bug 2** (related, found independently): a provisioned notification policy that references a
contact-point name that doesn't exist yet also makes Grafana crash on startup the same way
(`"receiver 'telegram' does not exist"`) - on the test cluster this wasn't initially noticed,
because by coincidence a contact point from an earlier debug session happened to still be in the
database there; on prod it failed immediately.

**Hence a deliberate exception to "everything automated"**: the Telegram contact point AND the
default notification policy are a one-time manual UI step, just like Uptime Kuma's initial
setup:
1. **Alerting → Contact points → New contact point** → integration "Telegram" → enter the bot
   token (via [@BotFather](https://t.me/BotFather)) + chat ID (via
   [@userinfobot](https://t.me/userinfobot) or
   `https://api.telegram.org/bot<TOKEN>/getUpdates`) → name EXACTLY `telegram`.
2. **Alerting → Notification policies → Default policy → Edit** → switch "Default contact point"
   to `telegram` → Save. **Easily forgotten** (happened exactly this way live): a successful
   click of the "Test" button on the contact point itself ONLY proves that the contact point
   works - without this second step, real alert firings keep going to the built-in
   `grafana-default-email` recipient and never reach Telegram.

Alert rules already run fully automatically (visible under Alerting → Alert rules), independent
of this manual step.

## Sealed Secrets: Secrets Versioned Encrypted in Git

Previously every secret was created manually via `kubectl create secret`, versioned nowhere -
in a total cluster loss, every individual value would have to be remembered. [Sealed
Secrets](https://github.com/bitnami-labs/sealed-secrets) (Bitnami) solves this: a controller runs
in the cluster with a self-generated key pair, and `kubeseal` uses it to encrypt a secret into a
`SealedSecret` custom resource that can SAFELY be committed to Git - only the controller in the
cluster can decrypt it.

**Installation**: the official manifest (`kubectl apply -f
https://github.com/bitnami-labs/sealed-secrets/releases/download/vX.Y.Z/controller.yaml`),
namespace `kube-system` (the manifest default) - a one-time, cluster-wide step like
ingress-nginx/Flux/cert-manager, NOT via `bootstrap-cluster.ps1`'s template apply loop.

**Workflow for a new secret**:
```
kubectl get secret <name> -n <namespace> -o yaml | kubeseal --format yaml --cert <pub-cert.pem> > k8s/sealed-secrets/<namespace>/<name>.yaml
git add k8s/sealed-secrets/<namespace>/<name>.yaml
kubectl apply -f k8s/sealed-secrets/<namespace>/<name>.yaml
```
`kubeseal --fetch-cert` against the running controller retrieves the public certificate for
sealing (used locally, NOT committed to the repo). `k8s/sealed-secrets/` deliberately lives
outside `bootstrap-cluster.ps1`'s automatic glob loop (its own subdirectory) - applying it is an
explicit, deliberate step.

**7 secrets migrated** (real, human-created credential material - NOT the CNPG/cnpg-system
internal CA/TLS secrets that the operator automatically regenerates itself):
`r2-backup-credentials`, `studylife-secrets`, `studylife-pg-app-secret` (all in
`studylife-scale`), `studylife-git-auth`, `studylife-registry-auth` (both in `flux-system`).

**Conflict found live (important)**: `studylife-tls`/`grafana-tls` were INITIALLY migrated too,
but had to be removed again - both are now managed by cert-manager (see the TLS section above)
and automatically renewed periodically. Simultaneous Sealed Secrets management would have caused
an ownership conflict at the next certificate renewal: the Sealed Secrets controller would have
reset the renewed (new) secret content back to the old state frozen at sealing time - which by
then would have already expired. Fixed via `kubectl delete sealedsecret --cascade=orphan`
(removes only the SealedSecret resource + its ownerReference claim, leaves the underlying secret
content, correctly managed by cert-manager, untouched). **Lesson**: a secret may only have
EXACTLY ONE managing controller - for automatically rotating certificates that's cert-manager,
not Sealed Secrets. For these two secrets, no Sealed Secrets backup is needed: on a cluster
rebuild, cert-manager automatically issues a fresh certificate from the already-versioned
`ClusterIssuer` config.

**Current live state: all 5 remaining secrets actively managed.** The Sealed Secrets controller
does NOT automatically adopt an already-existing secret that it doesn't manage (`Synced: False`,
`"Resource ... already exists and is not managed by SealedSecret"` - a safety feature of the
controller, not a bug). For each of the 5 secrets (`r2-backup-credentials`, `studylife-secrets`,
`studylife-pg-app-secret`, `studylife-git-auth`, `studylife-registry-auth`), adoption was forced:
both the original secret AND the SealedSecret resource itself were deleted (the latter necessary
because the controller "gives up" after the initial failed attempts and doesn't reconcile again
on its own once the original later disappears anyway - only a new `kubectl apply`/recreation of
the SealedSecret resource triggers a fresh reconcile attempt), then `kubectl apply` of the
SealedSecret file. Verified after each step: byte lengths identical to the previous value,
`Synced: True`, affected pods/cluster status unchanged and healthy. For
`studylife-pg-app-secret`, additionally a direct login test as the app user `studylife` with the
newly generated password (not just a byte comparison) - the CNPG cluster stayed continuously
"Cluster in healthy state" during the brief deletion, no pod restarts for Web/Worker.

`studylife-tls`/`grafana-tls` deliberately remain OUTSIDE this management (see above - cert-manager
is the sole owner here).

**CRITICAL**: the controller generates its own TLS key pair (a secret labeled
`sealedsecrets.bitnami.com/sealed-secrets-key` in the controller namespace) - if this key is
lost, ALL SealedSecrets become permanently undecryptable. As of this session, a backup of this
key exists only locally in a scratch directory - it needs to be moved to a secure location
outside the machine/repo (password manager, encrypted external storage).

## GitLab Integration: Kubernetes Agent + Flux Image Automation

**Migrated off GitLab entirely (2026-08).** CI/CD and the GitOps source/registry all moved to
GitHub Actions + GHCR (`ghcr.io/lukislp/studylife-server`, public package) - `Flux`'s
`GitRepository`/`ImageRepository` now point at `github.com/lukislp/studylife` / GHCR instead of
the self-hosted GitLab instance, and the `agentk`/KAS setup described below is no longer in use.
Kept in this section as historical incident documentation (the race-condition postmortem and KAS
troubleshooting steps below are still generally useful pattern knowledge for any GitOps/CI setup,
even though the specific GitLab plumbing they describe is gone) - see the up-to-date `k8s/flux/`
file list further down for the CURRENT configuration.

On the real Pi cluster, this replaces the previous Watchtower approach (the container polls for
new image tags itself) with GitOps: the CI system doesn't push anything actively; instead, the
cluster pulls new image tags itself and commits them back to the repo.

### Race Condition Found Live: A Version Tag Assigned Twice

`.gitlab-ci.yml`'s `get-version` job determines the next version via `npx semantic-release
--dry-run` - that's only a PREDICTION based on the current Git tag state, not an atomic
reservation. If two pipelines run through this section concurrently (two quick, back-to-back
commits/pushes - by no means rare in a session with many commits in a short time), both see the
same "current state" and BOTH predict the same `NEXT_VERSION`, even though they're building
different code. `docker:server` then pushes the Docker tag twice with different content - the
tag **string** is identical, but the image **digest** behind it isn't.

Found live on prod (symptom: occasional `SRI integrity check failed` / `Failed to fetch` when
loading the Blazor WASM file, usually after Ctrl+F5): two `studylife-web` pods both ran with tag
`1.14.1`, but `kubectl get pod ... -o
jsonpath='{.status.containerStatuses[0].imageID}'` showed two different SHA256 digests. Without
session affinity on the service, `ingress-nginx` serves alternately from both pods - if
`index.html`/the boot manifest comes from pod A, but the actual `.wasm` file comes from pod B (or
vice versa), Blazor's subresource-integrity check fails, because the hash expected in the
manifest doesn't match the actually delivered file content.

**Fix**: `resource_group: release-pipeline` on `get-version`, `publish:server`, `docker:server`,
and `semantic-release` - GitLab's built-in mechanism for serializing jobs with the same name
across PIPELINE boundaries. A second pipeline now waits until the first has run completely
through `semantic-release` (including a real, new Git tag) before entering `get-version` itself -
which means it always sees a "finished" tag state, never an intermediate state of a still-running
pipeline.

Immediate action on prod: deleted the pod with the "wrong"/older digest (Kubernetes recreated it
with the digest currently stored in the registry for the same tag, both pods consistent
afterward) - this only fixes the acute symptom, not the root cause, which is now fixed via
`resource_group`.

**Deliberately narrowly scoped** (not "fully automated GitOps" in the sense of "the entire `k8s/`
folder is synced automatically"): the files under `k8s/` are TEMPLATES with placeholders (e.g.,
`172.18.255.200-172.18.255.250` for MetalLB, `studylife.example.invalid` for the ingress host,
`studylife-k8s-dev` for the Postgres password) that only `bootstrap-cluster.ps1` fills in at
apply time. A naive Flux Kustomization sync directly against `k8s/**/*.yaml` would apply these
placeholders unchanged to the live cluster - wrong and potentially dangerous.

`kustomize-controller` is now (unlike originally planned) part of the installation -
`k8s/flux/deploy/kustomization.yaml` deploys automatically as soon as a file contains NO
placeholders: `04-web.yaml`/`05-worker.yaml` (image tags), `14-prometheus.yaml` (scrape config),
`17b-grafana-dashboards.yaml` (the human-readable dashboards), and `17d-grafana-alerting.yaml`
(alert rules). Still explicitly excluded: the rest of `k8s/` with real placeholders (including
`17-grafana.yaml` with the ingress host, `12-network-policies.yaml` as a deliberately
higher-risk category) - no `helm-controller`/`notification-controller`, no Kustomization
resource that would sync the rest of the folder.

**`[skip ci]` in the commit template is mandatory, not a style choice** (found live): without it,
every pure bot commit from `04-image-update-automation.yaml` (no app code changed, just an image
tag) would still trigger the entire GitLab pipeline again (build, tests, Docker rebuild+push of
an already-published image) - `.gitlab-ci.yml` has no path filters. `semantic-release`'s own
`chore(release): ... [skip ci]` commits already did this correctly; the Flux template only caught
up afterward.

**`k8s/flux/deploy/kustomization.yaml` also references `17c-grafana-community-dashboards.yaml`**
(correcting an outdated statement here: earlier versions of this document claimed the opposite)
- this file is >250KB (several complete community dashboard JSONs) and would fail against
client-side `kubectl apply` (e.g., via `bootstrap-cluster.ps1`) due to its 256KiB limit for the
`last-applied-configuration` annotation (found live). Flux's `kustomize-controller` uses
server-side apply (no such annotation, no limit) and is therefore the ONLY way this file ever
gets onto the cluster - see "Observability" above.

### GitLab Kubernetes Agent (agentk)

A pure connectivity/visibility component (agent-initiated, outbound - no inbound firewall opening
needed, important since the K3s cluster sits in the home network while GitLab is hosted
externally). Setup: Admin Area → GitLab Agent → register agent → run the provided `helm upgrade
--install ... --set config.token=...` command directly on the Pi (`pinode01`), NOT via the
kubeconfig copy on the laptop (which needs IP rewriting, see below). Set `export
KUBECONFIG=/etc/rancher/k3s/k3s.yaml` first - without this, Helm/kubectl falls back to the old
insecure `localhost:8080` default (`connection refused`).

**If registration fails with `rpc error: code = Unavailable desc = unavailable`**, two possible
causes (both already occurred live before):
1. KAS (Kubernetes Agent Server) isn't enabled on the GitLab instance itself - check with
   `grep gitlab_kas /etc/gitlab/gitlab.rb` in the GitLab container/host. Fix:
   `gitlab_kas['enable'] = true` + `gitlab_kas_external_url 'wss://<host>/-/kubernetes-agent/'`
   in `gitlab.rb`, then `gitlab-ctl reconfigure`.
2. KAS is running, but `gitlab_kas['gitlab_address']` is missing - KAS needs an INTERNAL address
   to the Rails/Workhorse API for this (default: `"http://localhost:8080"`), not the external
   HTTPS URL. Without this setting, KAS tries to address its own Rails API via the public
   hostname - which internally resolves to the container's own Docker bridge IP, where (depending
   on the reverse-proxy setup) nothing listens on 443 (`dial tcp <internal-ip>:443: connect:
   connection refused` in the KAS log, `/var/log/gitlab/gitlab-kas/current`). Fix: add the line,
   then `gitlab-ctl reconfigure` + restart the agent pods (`kubectl -n <agent-namespace> rollout
   restart deployment`).

### Installing Flux Image Automation + Deploy

Installed with component scoping (`source-controller`, `kustomize-controller`,
`image-reflector-controller`, `image-automation-controller` - explicitly WITHOUT
`helm-controller`/`notification-controller`, which would just tie up RAM unused on the 4GB node).
Manifest generated with the `flux` CLI (`flux install --components=source-controller,kustomize-controller
--components-extra=image-reflector-controller,image-automation-controller --export`), then the
resource limits were reduced by hand to the ~2-3× request ratio usual here (Flux's default of
1000m/1Gi per controller would be too generous for the 4GB node) - checked in under
`k8s/flux/00-install.yaml`. Important: Flux 2.9.x only serves `image.toolkit.fluxcd.io/v1` now
(no longer `v1beta2` as in older guides/examples).

`k8s/flux/`:
- `00-install.yaml` - the four controllers + their CRDs.
- `01-git-source.yaml` (`GitRepository`) - points at this repo, needs write access.
- `02-image-repository.yaml` (`ImageRepository`) - scans `ghcr.io/lukislp/studylife-server` for
  tags every 5 minutes. Public GHCR package - no `secretRef` needed for this one.
- `03-image-policy.yaml` (`ImagePolicy`) - selects the latest SemVer tag (semantic-release pushes
  pure SemVer tags with no "v" prefix, e.g., `1.5.8` - other tags like `latest`/`buildcache` are
  not valid SemVer and are automatically ignored).
- `04-image-update-automation.yaml` (`ImageUpdateAutomation`) - commits the new tag directly to
  `main` (no intermediate PR step - matches the previous Watchtower behavior). The
  commit-message template MUST use `.Changed.Objects`, not `.Updated.Images` - the latter was
  removed in Flux 2.9.x (occurred live on the Pi cluster: "template uses removed '.Updated'
  field").
- `05-kustomization.yaml` (`Kustomization`, `kustomize.toolkit.fluxcd.io`) - closes the last gap:
  `04-image-update-automation.yaml` commits the new tag to the Git repo, but never applies it to
  the cluster. Points at `k8s/flux/deploy/` (NOT the rest of `k8s/`, which still contains
  placeholders); `prune: true` is safe here since the scope is limited exactly to the resources
  referenced there via `kustomization.yaml`.
- `deploy/kustomization.yaml` - references `../../04-web.yaml`/`../../05-worker.yaml` via a
  relative path (the standard Flux base/overlay pattern, see fluxcd.io/flux/guides/repository-
  structure). **Audit finding O4:** used to also patch `imagePullPolicy` on both deployments to a
  fixed `Always` (the git-versioned original files permanently kept `Never`, only
  `bootstrap-cluster.ps1` wrote `Always` in memory before applying, never back into Git - so a
  direct apply without this patch would break the prod pull). `k8s/04-web.yaml`/
  `k8s/05-worker.yaml` now commit `Always` directly instead, so the patch is gone - this file is
  now a plain resource list. Cannot be tested directly locally with plain `kubectl kustomize`
  (its default safety restriction forbids `../` references outside the target directory) -
  workaroundable with `kubectl kustomize --load-restrictor LoadRestrictionsNone k8s/flux/deploy/`
  (the exact command `test-k8s-manifests` runs in CI, see `.github/workflows/ci-cd.yml`). Flux's
  `kustomize-controller` itself treats the entire Git checkout as the root, so `../../04-web.yaml`
  stays within the repo and is allowed there without restriction.
- `06-reconciler-rbac.yaml` - least-privilege `ClusterRole`/`ClusterRoleBinding` for
  `kustomize-controller`, replacing `00-install.yaml`'s stock `cluster-reconciler-flux-system`
  binding to `cluster-admin`. Scoped to exactly the apiGroups/kinds that `deploy/kustomization.yaml`
  actually applies (web/worker Deployments+Service+Certificate, Prometheus'
  ServiceAccount/ConfigMap/PVC/Deployment/Service plus its own narrow ClusterRole/
  ClusterRoleBinding, Grafana dashboard/alerting ConfigMaps) - the rest of `k8s/*.yaml`
  (Postgres/Gateway/MetalLB/NetworkPolicies/...) is applied by the operator directly via
  `bootstrap-cluster.ps1`, never through Flux, so `kustomize-controller` needs no permissions
  for any of it. Applying this is a **manual, supervised cutover** (see the comment block at
  the end of that file for the exact steps + rollback) - not something a routine `kubectl apply
  -f k8s/flux/` sweep should do blindly, since a missing rule would break reconciliation for
  every future deploy. **Caveat:** a future `flux install`/`flux bootstrap` re-run regenerates
  `00-install.yaml` and recreates the stock `cluster-admin` binding - the cutover (specifically,
  re-deleting `cluster-reconciler-flux-system`) needs to be redone after any such re-run.

**One secret must be created by hand BEFOREHAND** (`bootstrap-cluster.ps1 -WithFlux` checks for
this and aborts with a clear error message if it's missing - but deliberately never creates it
itself, since it's a real credential):

```bash
# Git write access - GitHub Personal Access Token (classic), scope "public_repo" is enough
# for a public repo (create under github.com -> Settings -> Developer settings -> Personal
# access tokens), then:
kubectl -n flux-system create secret generic studylife-git-auth \
  --from-literal=username=<your GitHub username> \
  --from-literal=password=<token value>
```

No separate registry secret is needed anymore - `ghcr.io/lukislp/studylife-server` is a public
GHCR package, and both `ImageRepository` (scanning) and the node's containerd (pulling) can reach
it without credentials.

**As a protected branch, `master` is presumably set to "Maintainers only" for push** - but the
access token's bot user needs push rights for the `ImageUpdateAutomation`. Either create the
token with role `Maintainer`, or (recommended, no need to recreate the token) switch under
**Settings -> Repository -> Protected branches -> master -> "Allowed to push and merge"** to
`Developers + Maintainers`. Symptom without this step: `ImageUpdateAutomation` stays
`Ready: False` with `failed to push to remote: ... pre-receive hook declined`.

Afterward: `.\bootstrap-cluster.ps1 -WithFlux <usual parameters>` (idempotent, can also be rerun
individually later with the same parameters) - or run the `kubectl apply -f k8s/flux/...`
commands individually directly on a node with `KUBECONFIG=/etc/rancher/k3s/k3s.yaml` (handy when
the prod kubeconfig isn't currently set up on the laptop). Check status: `kubectl -n
flux-system get gitrepository,kustomization,imagerepository,imagepolicy,imageupdateautomation` -
all five should show `Ready True`; use `flux logs` or `kubectl -n flux-system logs -l
app=image-automation-controller` if there are problems. After a template/manifest change to the
Flux CRs themselves, `kubectl -n flux-system delete pod -l app=<controller>` helps trigger the
next reconciliation immediately instead of waiting for the full `interval`.

**Validated locally** (Docker Desktop test cluster, with dummy credentials): all resources
structurally correct, controllers process them and, as expected, only fail on the (deliberately
wrong) authentication - no structural error. The deploy overlay build itself was additionally
checked in isolation with `kubectl kustomize --load-restrictor LoadRestrictionsNone
k8s/flux/deploy/`: `imagePullPolicy: Always` appeared correctly in the result at the time (via the
patch that existed back then, see the `deploy/kustomization.yaml` bullet above), all other fields
carried over unchanged. (Audit finding O4 later removed that patch in favor of committing `Always`
directly in `k8s/04-web.yaml`/`05-worker.yaml` - the same `kubectl kustomize` output is now
checked continuously by `test-k8s-manifests` in CI instead of only this one-off local check.)

**Verified live on the Pi cluster** (not just locally): GitRepository, ImageRepository,
ImagePolicy (correctly resolved `1.13.3` as the latest tag), and ImageUpdateAutomation ran
successfully after fixing the commit template and the protected-branch rule, including a real
commit back to `master`.

## Deliberate Scope Cuts

- **Raw DB backup/restore via `BackupController`** stays SQLite-only (still responds with 501 in
  Postgres mode) - a dedicated `pg_dump` endpoint would be its own project. For Postgres mode,
  there's now instead CNPG's own WAL archiving/backup path to Cloudflare R2 (see "Postgres
  Backups to Cloudflare R2" above) - active on prod, backup and restore verified live.
- **No distributed rate limiting** - the existing per-pod in-memory limiter remains as a
  backstop; real multi-pod limiting belongs at the ingress/load balancer.
- **Dynamic worker autoscaling** (e.g., an HPA based on user count) not built - the partitioning
  itself scales to any number of replicas (see "Worker Scaling" above), but
  `spec.replicas`/`Worker__ReplicaCount` are maintained manually, not adjusted automatically
  based on load.
- **`dotnet test` stays on SQLite** - fast, no external dependency. The Postgres-specific parts
  (search strategy, DateTime conversion, secrets race, claim-first race) have their own,
  targeted tests (`SystemSecretsServiceTests.cs`, `BackgroundTaskServiceClaimTests.cs`), but no
  Testcontainers-based Postgres suite for the whole app.
- **Migration overhead**: future schema changes now need ONE migration EACH per provider
  (`dotnet ef migrations add X --context StudyLifeDb` AND `--context StudyLifeDbPostgres
  --output-dir Migrations/Postgres`) - no automation, a deliberate tradeoff accepted for this
  learning branch.
- **CNPG pooler metrics inactive** - port 9127 is declared in the pod spec, but returns
  "connection refused" (checked live, not a single log entry about the metrics server, no
  `LISTEN` socket in `/proc/net/tcp` in the pgbouncer container - the built-in exporter simply
  doesn't start in our CNPG 1.24.0/pgbouncer 1.23.0 combination, even though the CNPG docs
  describe this as automatic). A standard `prometheus-community/pgbouncer_exporter` sidecar
  would be the usual workaround, but fails structurally here: `pg_hba.conf` allows the
  "pgbouncer" admin database (which provides `SHOW STATS`/`SHOW POOLS`) exclusively via the
  local Unix socket with `peer` auth, but a sidecar would need a TCP connection with a password -
  a clean fix would be either a CNPG version upgrade (its own risk for the entire DB cluster) or
  a sidecar sharing the internal Unix-socket mount and using exactly the right UID (a CNPG
  internals hack that could potentially break with any operator update) - disproportionate for
  pure pooler connection statistics, when DB availability itself is already covered via port
  9187. No Prometheus job/dashboard for this until CNPG fixes it upstream.
- **No central, Alertmanager-spanning alerting for GitLab itself** - GitLab (externally hosted,
  Docker-based) is a single point of failure for the entire Flux GitOps chain (image updates,
  dashboard/config auto-deploy) - whether/how GitLab itself is secured is outside the scope of
  this document.
- **piwatch runs without `runAsNonRoot`** (unlike studylife-web/-worker, whose Dockerfile already
  sets `USER app`) - the piwatch image has no `USER` directive, so it would crash immediately
  with `runAsNonRoot: true`. `allowPrivilegeEscalation: false` + capability drop are still set
  (safe regardless of the image); full non-root hardening would require a Dockerfile change +
  rebuild in the separate piwatch repo. For the node-agent DaemonSet, additionally deliberately
  NO capability drop (unlike the piwatch deployment): it reads `/host/proc`, `/host/sys`,
  `/host/root` as root with no `USER` of its own - without `CAP_DAC_OVERRIDE`, root would be
  subject to the same file permission checks as a normal user, which could create new read gaps
  on individual non-world-readable `/proc` paths that couldn't be verified live without
  production risk.
- **Pod Security Standards for `monitoring` deliberately only `warn`/`audit` (baseline), NOT
  `enforce`** - piwatch-node-agent and node-exporter legitimately need hostPath volumes
  (`/sys`,`/proc`,`/`), which "baseline" completely forbids; an `enforce` would block both
  DaemonSets on the next rollout. `studylife-scale`, in contrast, has `enforce: baseline` (+
  `warn`/`audit: restricted` for visibility into further hardening options like
  `readOnlyRootFilesystem`), because no workload there needs hostPath.
- **K3s patch level `v1.36.2+k3s1` checked via web research** (2026-07-18): actually the current
  stable version per `github.com/k3s-io/k3s/releases` - no upgrade needed, but also no automated
  check set up for this; worth periodically re-checking manually rather than relying on this
  snapshot.
- **kube-bench (CIS benchmark, profile `k3s-cis-1.9`) run once against the control-plane node**:
  28 FAIL hits, most of them false positives according to K3s' own hardening guide (K3s bundles
  the API server/controller manager/scheduler into one binary without the individual, classic CLI
  flags kube-bench expects to find - anonymous-auth, authorization-mode, TLS certificates, etc.
  are already correctly hardened internally). The one genuine hit safely fixable via `kubectl`
  (default ServiceAccount token automount, CIS 5.1.5/5.1.6) was fixed. **Deliberately NOT
  implemented** (all three need a K3s server config change + restart on the single control-plane
  node with no failover - explicitly declined once the risk was clearly stated):
  - Secrets encryption in the datastore itself (Sealed Secrets only protects the path through
    Git, not the decrypted values in the running datastore).
  - Kubernetes API audit logging (`--audit-log-path`/`--audit-policy-file`).
  - Disabling profiling + enforcing strong TLS cipher suites on the API server (kube-bench
    1.2.15/1.3.2/1.4.1/1.2.29).
  - Also possible without risk, but not implemented for the same reason (SSH access needed):
    file permissions on the PKI certificates (`chmod 600`, kube-bench 1.1.9/1.1.10/1.1.20).

## Mapping: The Same Building Blocks on Other Platforms

The app-side rework (provider switches, Redis cache, DB-backed secrets) is completely
deployment-independent - the same configuration works identically with multiple VPS instances or
AWS instead of Kubernetes:

| Kubernetes concept | AWS equivalent | Multiple VPS + docker-compose |
|---|---|---|
| `Deployment` (5 Web replicas) | ECS service (Fargate, `desiredCount: 5`) | 1 `server` container per VPS each, `docker compose up` |
| MetalLB (stable IP on bare metal) | not needed - ALB/NLB automatically assigns a real address, no equivalent needed | a fixed VPS IP or DNS entry that the external proxy targets |
| NGINX Gateway Fabric / Gateway API (2 data-plane replicas, TLS termination) | Application Load Balancer (ALB) + ACM certificate, or the AWS Gateway API controller | an external/own nginx proxy takes on the same role |
| Cloudflare Origin CA certificate | ACM certificate (publicly trusted, managed by AWS) | a Let's Encrypt certificate on the external proxy |
| CNPG `Cluster` (1 primary + 2 replicas) | RDS for PostgreSQL Multi-AZ + read replicas (managed) | Patroni/repmgr across multiple VPS, or a hoster's managed DB |
| CNPG backup to Cloudflare R2 | RDS automated backups + point-in-time recovery (built in) | a `pg_dump` cron job to S3/R2 |
| Redis `StatefulSet` cluster (3M+3R) | ElastiCache for Redis, cluster mode enabled (managed) | Redis Cluster across multiple VPS, or a hoster's managed Redis |
| `Worker` `Deployment` (3 replicas, Redis shard claim + claim-first safety net) | ECS service with `desiredCount: 3` - identical Redis claim mechanism, NO platform-specific adjustment needed (no more reliance on task IDs/hostnames) | multiple VPS (or containers) with `Worker__Enabled=true`, `Worker__ReplicaCount` set to match the count - coordinate themselves via Redis |
| Uptime Kuma + self-hosted Prometheus/Grafana/Loki | CloudWatch Alarms + Amazon Managed Grafana/Prometheus | the same options, self-hosted or on a dedicated VPS |
| CNPG `Pooler` (PgBouncer) | RDS Proxy (managed) | PgBouncer as its own service on a VPS |
| NetworkPolicies | Security groups + NACLs (VPC level) | iptables/nftables or an overlay network (WireGuard/Tailscale) with its own ACL |
| PodDisruptionBudgets | ECS deployment circuit breaker / `minHealthyPercent` | no direct equivalent - discipline the rolling-update order by hand |
| `ConfigMap`/`Secret` | ECS task-definition environment/Secrets Manager | a `.env` file / Docker Compose `environment:` |

For real cloud deployments, a managed database (RDS/Cloud SQL) still often remains the better
choice over a self-managed CNPG cluster INSIDE your own Kubernetes - it saves on patch
management, backup infrastructure (S3 integration for WAL archiving), and building up expertise
in running Postgres itself. CNPG is the right call when Kubernetes is the target platform anyway
and you deliberately want to run the database in the same cluster (the learning goal of this
branch) - for purely production requirements without this learning aspect, "managed DB outside
the cluster" is usually the more pragmatic default choice.

## Multi-Repo GitOps: Onboarding studylife-ai as a Second Flux Source

`studylife-ai` (a separate Python microservice + repo, see its own `docs/decisions.md`) needed
Flux-managed continuous deployment too, without standing up a second Flux install. Flux supports
this natively: multiple `GitRepository` sources can coexist under the same `flux-system`
install, each with its own `ImageRepository`/`ImagePolicy`/`ImageUpdateAutomation`/`Kustomization`
chain - `k8s/flux/06`-`10-studylife-ai-*.yaml` mirror `01`-`05` exactly, just pointing at
`github.com/lukislp/studylife-ai.git` instead of this repo. It reuses the existing
`studylife-git-auth` secret rather than a new PAT [owner: user] - a classic GitHub PAT
(`public_repo` scope) is never scoped to a single repo by GitHub design anyway, so it already
covered `lukislp/studylife-ai` too; the trade-off (both `GitRepository` sources now share one
credential - rotating/revoking it affects both at once) was accepted deliberately for this
personal homelab setup, not something a stricter multi-tenant setup should copy unexamined.

**The one real constraint that shaped the split**: `06-reconciler-rbac.yaml`'s least-privilege
`ClusterRole` only grants `kustomize-controller` permissions on `configmaps`/
`persistentvolumeclaims`/`services`/`deployments`/`replicasets` (read-only) - explicitly NOT
`namespaces`, `secrets`, or `networkpolicies`. That's exactly why `05-kustomization.yaml` here
only ever applied `k8s/flux/deploy/` (a curated subset), never the whole `k8s/` folder - and the
same RBAC boundary applies identically to a second onboarded repo, regardless of which repo it
is. `studylife-ai`'s own `k8s/flux-deploy/kustomization.yaml` therefore also only references its
`ConfigMap`/Qdrant/app `Deployment+PVC+Service` files - its `Namespace`/`Secret`/
`NetworkPolicy` files stay bootstrap-only, applied once by hand, matching this repo's own split.
No RBAC changes were needed here at all: the resource kinds studylife-ai's Flux-managed subset
uses were already covered by the existing `ClusterRole`.

**Local validation caveat found while building this**: `kubectl kustomize` (and by extension
`kustomize build` standalone) refuses by default to resolve a `kustomization.yaml`'s `resources:`
entries that point OUTSIDE its own directory (`../../04-web.yaml`, the same pattern this repo's
own `k8s/flux/deploy/kustomization.yaml` already uses in production) - `security; file '...' is
not in or below '...'`. This is a `kustomize` CLI default (`--load-restrictor
LoadRestrictionsNone` overrides it locally for a sanity check), not a Flux behavior - Flux's
`kustomize-controller` treats the whole Git checkout as one trust boundary and reconciles the
existing cross-directory pattern here in production without issue, confirmed by the live
`chore(image): auto-update` commits already in this repo's history. Worth knowing before
assuming a local `kubectl kustomize` failure means the manifests are broken.
