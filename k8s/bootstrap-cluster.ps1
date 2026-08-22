<#
.SYNOPSIS
  Bootstraps StudyLife's own app stack on an already cluster-wide-provisioned K3s cluster.

.DESCRIPTION
  Runs on your LAPTOP (PowerShell), NOT on a Pi - assumes homelab-infra's own
  provisioning/bootstrap-cluster.ps1 has already run (CNPG operator, MetalLB, cert-manager
  issuers, the shared Gateway, the monitoring stack), plus cert-manager and NGINX Gateway Fabric
  themselves per their own upstream docs (see homelab-infra/cluster/00c-nginx-gateway-fabric.md).
  This is the app-specific half of what used to be a single, combined bootstrap script - part of
  the cluster-wide-infra split (see homelab-infra's README.md). Covers the StudyLife manifests
  themselves (with your real Postgres password/image substituted in place of the test
  placeholders) and the Redis cluster bootstrap.

  NOT included (deliberately, see docs/SCALING.md):
    - R2 backup CronJob (k8s/08-scheduled-backup.yaml) - needs a manually created
      R2 secret plus the backup block in k8s/02-postgres.yaml uncommented beforehand. Include it anyway with
      -WithR2Backup (only useful if both are already done).
    - TLS certificate (Cloudflare Origin CA) - the "studylife-tls" secret referenced in
      k8s/07c-gateway.yaml does not correspond to an automatically created secret; without it,
      the Gateway listener still runs per its own comment, just without TLS termination.
    - Flux (GitOps image updates, k8s/flux/) - needs the studylife-git-auth/
      studylife-registry-auth secrets created manually BEFOREHAND (real credentials, see
      docs/SCALING.md) with real credentials, which this script deliberately never creates
      itself. Include it anyway with -WithFlux (only useful if both secrets already exist).

  Idempotent: kubectl apply is idempotent by nature; the Redis cluster bootstrap checks
  beforehand whether a cluster already exists and skips itself otherwise.

.PARAMETER PostgresPassword
  Real Postgres password (replaces the test placeholder "studylife-k8s-dev" in the repo).

.PARAMETER RegistryImage
  Fully qualified image, default = the same image the existing single-instance
  deployment already uses via Watchtower.

.EXAMPLE
  .\bootstrap-cluster.ps1 -PostgresPassword "your-own-secure-password"
#>
param(
    [string]$RegistryImage = "registry.example.com/studylife/server:latest",
    [Parameter(Mandatory = $true)][string]$PostgresPassword,
    [switch]$WithR2Backup,
    [switch]$WithFlux
)

$ErrorActionPreference = "Stop"
$K8sDir = $PSScriptRoot

function Wait-Deployment {
    param([string]$Namespace, [string]$Name, [int]$TimeoutSec = 180)
    Write-Host "Waiting for deployment ${Namespace}/${Name}..."
    kubectl -n $Namespace rollout status "deployment/$Name" --timeout="${TimeoutSec}s"
}

Write-Host "=== [1/5] Checking cluster reachability ==="
kubectl get nodes
if ($LASTEXITCODE -ne 0) {
    throw "kubectl cannot reach the cluster - is KUBECONFIG set? (`$env:KUBECONFIG)"
}

Write-Host ""
Write-Host "=== [2/5] Applying StudyLife manifests (with your own values instead of the test placeholders) ==="
# A plain PowerShell array + "-contains" instead of List<string>.AddRange() - an @(...) array
# literal is an untyped Object[] at runtime, which .NET's generic AddRange(IEnumerable<string>)
# doesn't always accept (type conversion error). "-contains" doesn't have this problem.
$skip = @()
if (-not $WithR2Backup) { $skip += "08-scheduled-backup.yaml" }

$files = Get-ChildItem $K8sDir -Filter "*.yaml" | Sort-Object Name
foreach ($f in $files) {
    if ($skip -contains $f.Name) {
        Write-Host "  skipping $($f.Name)"
        continue
    }
    $content = Get-Content $f.FullName -Raw
    switch ($f.Name) {
        "01-config-and-secret.yaml" {
            $content = $content -replace "studylife-k8s-dev", $PostgresPassword
        }
        "04-web.yaml" {
            $content = $content -replace "image: studylife-server:scale-v\d+", "image: $RegistryImage"
            $content = $content -replace "imagePullPolicy: Never", "imagePullPolicy: Always"
        }
        "05-worker.yaml" {
            $content = $content -replace "image: studylife-server:scale-v\d+", "image: $RegistryImage"
            $content = $content -replace "imagePullPolicy: Never", "imagePullPolicy: Always"
        }
    }
    Write-Host "  apply: $($f.Name)"
    $content | kubectl apply -f -
    if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed for $($f.Name)" }
}

Write-Host ""
Write-Host "=== [3/5] Waiting for studylife-scale pods ==="
Start-Sleep -Seconds 10
# --field-selector excludes CNPG job pods (initdb/join) that have already completed - they never
# become "Ready" (they run through once and then terminate); "kubectl wait --for=condition=Ready"
# would otherwise hang on them until the timeout, even though everything is actually fine.
kubectl -n studylife-scale wait --for=condition=Ready pod --all --timeout=600s `
    --field-selector="status.phase!=Succeeded,status.phase!=Failed"

Write-Host ""
Write-Host "=== [4/5] Bootstrapping Redis cluster (only if not already done) ==="
# Specify -c redis explicitly: the pods have 2 containers (redis, redis-exporter); without -c,
# kubectl writes an informational "Defaulted container..." line to STDERR - under Windows PowerShell
# 5.1, EVERY native stderr line gets wrapped in an ErrorRecord, which with $ErrorActionPreference =
# "Stop" aborts the script even though redis-cli itself would have succeeded. -c avoids the line
# entirely, rather than just redirecting it away (2>$null alone isn't reliably sufficient for that).
$clusterInfo = kubectl -n studylife-scale exec redis-cluster-0 -c redis -- redis-cli cluster info 2>$null
if ($clusterInfo -match "cluster_state:ok") {
    Write-Host "Redis cluster already exists - skipping."
} else {
    # --cluster-replicas 1: k8s/03-redis.yaml expects 3 masters + 3 replicas (6 pods) - with
    # --cluster-replicas 0, all 6 would end up as independent masters with no redundancy.
    $ipsRaw = kubectl -n studylife-scale get pods -l app=redis-cluster -o jsonpath='{range .items[*]}{.status.podIP}:6379 {end}'
    $ipsArray = $ipsRaw.Trim().Split(" ", [System.StringSplitOptions]::RemoveEmptyEntries)
    kubectl -n studylife-scale exec -i redis-cluster-0 -c redis -- redis-cli --cluster create $ipsArray --cluster-replicas 1 --cluster-yes
}

if ($WithFlux) {
    Write-Host ""
    Write-Host "=== [5/5] Flux (GitOps image updates) ==="
    # Both secrets must be created by hand BEFOREHAND (real credentials, see
    # docs/SCALING.md) - this script deliberately never creates them itself.
    # studylife-git-auth is shared by every GitRepository on the cluster (see homelab-infra's
    # sealed-secrets/), not studylife-specific despite the name.
    kubectl -n flux-system get secret studylife-git-auth, studylife-registry-auth 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Secrets 'studylife-git-auth'/'studylife-registry-auth' are missing in namespace 'flux-system' - see docs/SCALING.md, Flux section."
    }
    # Flux's own install manifest + the shared reconciler RBAC + homelab-infra's own
    # GitRepository/Kustomization now live in homelab-infra (cluster-wide-infra split) -
    # bootstrap that repo's provisioning/bootstrap-cluster.ps1 -WithFlux first, then this block
    # for studylife's own wiring only.
    kubectl apply -f "$K8sDir/flux/01-git-source.yaml" -f "$K8sDir/flux/02-image-repository.yaml" -f "$K8sDir/flux/03-image-policy.yaml" -f "$K8sDir/flux/04-image-update-automation.yaml" -f "$K8sDir/flux/05-kustomization.yaml"
    # studylife-ai/studylife-mcp/piwatch/unifiprotectdashboard each own their own Flux wiring in
    # their own repo now (k8s/flux/ or deploy/flux/ there) - this script no longer applies it on
    # their behalf, see each repo's own bootstrap/README for onboarding a fresh cluster.
}

# From here on, only informational output - none of these lines should still cause the script to
# end as a failure due to stderr noise (see the comment above at the Redis check), even though the
# actual bootstrap has already fully completed by this point.
$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "=== Setup code ==="
kubectl -n studylife-scale logs -l app=studylife-web --all-containers --prefix 2>$null | Select-String -Context 0, 2 "setup code"

Write-Host ""
Write-Host "Done. TLS is NOT active yet (the studylife-tls secret does not exist) - the app runs"
Write-Host "over HTTP only until then. See homelab-infra/cluster/02-gateway.yaml for the shared"
Write-Host "Gateway's hostnames and IP (kubectl -n nginx-gateway get svc studylife-gateway-nginx)."
