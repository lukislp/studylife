<#
.SYNOPSIS
  Bootstraps the complete StudyLife K8s stack on an already-running K3s cluster.

.DESCRIPTION
  Runs on your LAPTOP (PowerShell), NOT on a Pi - assumes at least the
  K3s server node has already been set up via setup-node.sh and KUBECONFIG points at the
  cluster (see docs/SCALING.md, "Getting the kubeconfig onto your laptop"). Covers phases 3-6 of the
  procedure described there: the CNPG operator, MetalLB, ingress-nginx, the StudyLife manifests
  themselves (with real values substituted in place of the test placeholders), Redis cluster bootstrap, and the
  monitoring stack.

  NOT included (deliberately, see docs/SCALING.md):
    - Loki/Promtail (log aggregation) - Promtail demonstrably yields 0 targets, so it's
      skipped by default. Include it anyway with -WithLoki.
    - R2 backup CronJob (k8s/08-scheduled-backup.yaml) - needs a manually created
      R2 secret plus the backup block in k8s/02-postgres.yaml uncommented beforehand. Include it anyway with
      -WithR2Backup (only useful if both are already done).
    - TLS certificate (Cloudflare Origin CA) - the "studylife-tls" secret referenced in k8s/07-ingress.yaml
      does not correspond to an automatically created secret; without it, the ingress resource still runs
      per its own comment, just without TLS termination.
    - Flux (GitOps image updates, k8s/flux/) - needs secrets created manually BEFOREHAND
      ("studylife-git-auth", "studylife-registry-auth" in the "flux-system" namespace, see
      docs/SCALING.md) with real credentials, which this script deliberately never creates itself. Include it
      anyway with -WithFlux (only useful if both secrets already exist).

  Idempotent: kubectl apply is idempotent by nature; the Redis cluster bootstrap checks
  beforehand whether a cluster already exists and skips itself otherwise.

.PARAMETER MetalLBRange
  IP range from your actual home-network subnet that the router's DHCP does NOT hand out, e.g.
  "192.168.4.240-192.168.4.250".

.PARAMETER IngressHost
  Hostname under which the app should later be reachable, e.g. "studylife.home.lan".

.PARAMETER PostgresPassword
  Real Postgres password (replaces the test placeholder "studylife-k8s-dev" in the repo).

.PARAMETER RegistryImage
  Fully qualified image, default = the same image the existing single-instance
  deployment already uses via Watchtower.

.EXAMPLE
  .\bootstrap-cluster.ps1 -MetalLBRange "192.168.4.240-192.168.4.250" -IngressHost "studylife.home.lan" -PostgresPassword "your-own-secure-password"
#>
param(
    [string]$RegistryImage = "registry.example.com/studylife/server:latest",
    [Parameter(Mandatory = $true)][string]$MetalLBRange,
    [Parameter(Mandatory = $true)][string]$IngressHost,
    [Parameter(Mandatory = $true)][string]$PostgresPassword,
    # Optional: public domain that comes in via an external reverse proxy (e.g. Nginx Proxy
    # Manager), if that proxy passes through the original client Host header unchanged
    # instead of rewriting it to $IngressHost - see the comment in k8s/07-ingress.yaml.
    [string]$PublicHost,
    # Optional: hostname for Grafana (k8s/17-grafana.yaml), e.g. "grafana.home.lan". Without this
    # parameter the placeholder "grafana.example.invalid" stays in place - the ingress rule then
    # matches nothing, and Grafana remains reachable only via "kubectl port-forward".
    [string]$GrafanaHost,
    # Optional: public domain for Grafana via an external reverse proxy - see the
    # comment in k8s/17-grafana.yaml (analogous to -PublicHost for the app itself).
    [string]$GrafanaPublicHost,
    # Optional: hostname for Uptime Kuma (k8s/09-uptime-kuma.yaml), e.g. "uptime.home.lan".
    [string]$UptimeKumaHost,
    # Optional: public domain for Uptime Kuma via an external reverse proxy.
    [string]$UptimeKumaPublicHost,
    [switch]$WithLoki,
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

Write-Host "=== [1/8] Checking cluster reachability ==="
kubectl get nodes
if ($LASTEXITCODE -ne 0) {
    throw "kubectl cannot reach the cluster - is KUBECONFIG set? (`$env:KUBECONFIG)"
}

Write-Host ""
Write-Host "=== [2/8] CloudNativePG operator ==="
kubectl apply --server-side -f https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/release-1.29/releases/cnpg-1.29.1.yaml
Wait-Deployment -Namespace "cnpg-system" -Name "cnpg-controller-manager"

Write-Host ""
Write-Host "=== [3/8] Installing MetalLB ==="
kubectl apply -f https://raw.githubusercontent.com/metallb/metallb/v0.14.9/config/manifests/metallb-native.yaml
Wait-Deployment -Namespace "metallb-system" -Name "controller"

Write-Host ""
Write-Host "=== [4/8] Installing + patching ingress-nginx ==="
kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.11.3/deploy/static/provider/cloud/deploy.yaml
Wait-Deployment -Namespace "ingress-nginx" -Name "ingress-nginx-controller"

kubectl -n ingress-nginx scale deployment ingress-nginx-controller --replicas=2

# kubectl patch -p '<json>' with embedded double quotes arrives broken under Windows
# PowerShell when calling the NATIVE kubectl.exe (for native processes, Windows builds
# a single command-line STRING instead of a real argv[] array like Unix does -
# embedded quotes aren't reliably re-escaped in the process). --patch-file with
# a real temp file sidesteps the problem entirely. -Encoding ascii is used deliberately instead of utf8, since
# PowerShell 5.1's "utf8" writes a BOM that kubectl/JSON parsers would read as an invalid first character -
# the patch content here is pure ASCII anyway.
$patchDir = Join-Path $env:TEMP "studylife-ingress-patches"
New-Item -ItemType Directory -Force -Path $patchDir | Out-Null

$antiAffinityPatchFile = Join-Path $patchDir "anti-affinity.json"
'{"spec":{"template":{"spec":{"affinity":{"podAntiAffinity":{"preferredDuringSchedulingIgnoredDuringExecution":[{"weight":100,"podAffinityTerm":{"labelSelector":{"matchLabels":{"app.kubernetes.io/component":"controller","app.kubernetes.io/name":"ingress-nginx"}},"topologyKey":"kubernetes.io/hostname"}}]}}}}}}' |
    Set-Content -Path $antiAffinityPatchFile -NoNewline -Encoding ascii
kubectl -n ingress-nginx patch deployment ingress-nginx-controller --type=merge "--patch-file=$antiAffinityPatchFile"

$resourcesPatchFile = Join-Path $patchDir "resources.json"
'{"spec":{"template":{"spec":{"containers":[{"name":"controller","resources":{"requests":{"cpu":"50m","memory":"90Mi"},"limits":{"cpu":"200m","memory":"200Mi"}}}]}}}}' |
    Set-Content -Path $resourcesPatchFile -NoNewline -Encoding ascii
kubectl -n ingress-nginx patch deployment ingress-nginx-controller --type=strategic "--patch-file=$resourcesPatchFile"

# --enable-metrics=true (the default in the official cloud deploy is false, otherwise an empty Grafana dashboard) -
# args must be replaced entirely, merging individual flags isn't possible. "$(POD_NAMESPACE)" here
# is UNQUOTED on the PowerShell side (single quotes on the outside) - it stays reserved for the
# nginx-ingress-controller itself, no PowerShell variable expansion.
$argsPatchFile = Join-Path $patchDir "args.json"
'{"spec":{"template":{"spec":{"containers":[{"name":"controller","args":["/nginx-ingress-controller","--publish-service=$(POD_NAMESPACE)/ingress-nginx-controller","--election-id=ingress-nginx-leader","--controller-class=k8s.io/ingress-nginx","--ingress-class=nginx","--configmap=$(POD_NAMESPACE)/ingress-nginx-controller","--validating-webhook=:8443","--validating-webhook-certificate=/usr/local/certificates/cert","--validating-webhook-key=/usr/local/certificates/key","--enable-metrics=true"]}]}}}}' |
    Set-Content -Path $argsPatchFile -NoNewline -Encoding ascii
kubectl -n ingress-nginx patch deployment ingress-nginx-controller --type=strategic "--patch-file=$argsPatchFile"

Wait-Deployment -Namespace "ingress-nginx" -Name "ingress-nginx-controller"

Write-Host ""
Write-Host "=== [5/8] Applying StudyLife manifests (with your own values instead of the test placeholders) ==="
# A plain PowerShell array + "-contains" instead of List<string>.AddRange() - an @(...) array
# literal is an untyped Object[] at runtime, which .NET's generic AddRange(IEnumerable<string>)
# doesn't always accept (type conversion error). "-contains" doesn't have this problem.
$skip = @()
if (-not $WithLoki) { $skip += @("18-loki.yaml", "19-promtail.yaml") }
if (-not $WithR2Backup) { $skip += "08-scheduled-backup.yaml" }
# Always skipped (cannot be enabled via a switch): the vendored community dashboards
# are >250KB and thus blow past "kubectl apply"'s client-side last-applied-configuration
# annotation (256KiB limit, found in production). Instead it's applied EXCLUSIVELY via Flux's own
# Kustomization (k8s/flux/deploy/), which uses server-side apply and doesn't have this limit at all.
$skip += "17c-grafana-community-dashboards.yaml"

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
        "06-metallb-config.yaml" {
            $content = $content -replace "172\.18\.255\.200-172\.18\.255\.250", $MetalLBRange
        }
        "07-ingress.yaml" {
            $content = $content -replace "studylife\.example\.invalid", $IngressHost
            if ($PublicHost) {
                $content = $content -replace "studylife-public\.example\.invalid", $PublicHost
            }
        }
        "17-grafana.yaml" {
            if ($GrafanaHost) {
                $content = $content -replace "grafana\.example\.invalid", $GrafanaHost
            }
            if ($GrafanaPublicHost) {
                $content = $content -replace "grafana-public\.example\.invalid", $GrafanaPublicHost
            }
        }
        "09-uptime-kuma.yaml" {
            if ($UptimeKumaHost) {
                $content = $content -replace "uptime\.example\.invalid", $UptimeKumaHost
            }
            if ($UptimeKumaPublicHost) {
                $content = $content -replace "uptime-public\.example\.invalid", $UptimeKumaPublicHost
            }
        }
    }
    Write-Host "  apply: $($f.Name)"
    $content | kubectl apply -f -
    if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed for $($f.Name)" }
}

Write-Host ""
Write-Host "=== [6/8] Waiting for studylife-scale pods ==="
Start-Sleep -Seconds 10
# --field-selector excludes CNPG job pods (initdb/join) that have already completed - they never
# become "Ready" (they run through once and then terminate); "kubectl wait --for=condition=Ready"
# would otherwise hang on them until the timeout, even though everything is actually fine.
kubectl -n studylife-scale wait --for=condition=Ready pod --all --timeout=600s `
    --field-selector="status.phase!=Succeeded,status.phase!=Failed"

Write-Host ""
Write-Host "=== [7/8] Bootstrapping Redis cluster (only if not already done) ==="
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
    Write-Host "=== [8/8] Flux (GitOps image updates) ==="
    # Both secrets must be created by hand BEFOREHAND (real credentials, see
    # docs/SCALING.md) - this script deliberately never creates them itself.
    # studylife-git-auth is reused for the studylife-ai GitRepository too (M5, studylife-ai
    # onboarding - see studylife-ai's own docs/decisions.md "M5 - Deployment design")
    # [owner: user] - no separate secret needed.
    kubectl -n flux-system get secret studylife-git-auth, studylife-registry-auth 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Secrets 'studylife-git-auth'/'studylife-registry-auth' are missing in namespace 'flux-system' - see docs/SCALING.md, Flux section."
    }
    kubectl apply -f "$K8sDir/flux/00-install.yaml"
    Wait-Deployment -Namespace "flux-system" -Name "source-controller"
    Wait-Deployment -Namespace "flux-system" -Name "kustomize-controller"
    Wait-Deployment -Namespace "flux-system" -Name "image-reflector-controller"
    Wait-Deployment -Namespace "flux-system" -Name "image-automation-controller"
    kubectl apply -f "$K8sDir/flux/01-git-source.yaml" -f "$K8sDir/flux/02-image-repository.yaml" -f "$K8sDir/flux/03-image-policy.yaml" -f "$K8sDir/flux/04-image-update-automation.yaml" -f "$K8sDir/flux/05-kustomization.yaml"
    kubectl apply -f "$K8sDir/flux/06-studylife-ai-git-source.yaml" -f "$K8sDir/flux/07-studylife-ai-image-repository.yaml" -f "$K8sDir/flux/08-studylife-ai-image-policy.yaml" -f "$K8sDir/flux/09-studylife-ai-image-update-automation.yaml" -f "$K8sDir/flux/10-studylife-ai-kustomization.yaml"
}

# From here on, only informational output - none of these lines should still cause the script to
# end as a failure due to stderr noise (see the comment above at the Redis check), even though the
# actual bootstrap has already fully completed by this point.
$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "=== Setup code ==="
kubectl -n studylife-scale logs -l app=studylife-web --all-containers --prefix 2>$null | Select-String -Context 0, 2 "setup code"

Write-Host ""
Write-Host "=== Ingress / MetalLB IP ==="
kubectl -n ingress-nginx get svc ingress-nginx-controller

Write-Host ""
Write-Host "=== Monitoring ==="
Write-Host "Grafana (temporary): kubectl -n monitoring port-forward svc/grafana 3000:80"
Write-Host "  Login admin/admin, change it right after the first login."

Write-Host ""
Write-Host "Done. TLS is NOT active yet (the studylife-tls secret does not exist) - the app runs"
Write-Host "over HTTP only until then. Point a DNS/hosts entry for '$IngressHost' at the EXTERNAL-IP above,"
Write-Host "or to test: curl.exe -k -H `"Host: $IngressHost`" http://<EXTERNAL-IP>/"
