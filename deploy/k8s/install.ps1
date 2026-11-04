# Instala HelpDesk completo en el clúster local (deploy/k8s/cluster.yaml). Se puede correr de nuevo: actualiza lo que cambió.
# La primera vez tarda: el clúster no comparte imágenes con Docker, y baja las de SQL Server y Keycloak por su cuenta.
#   powershell -File deploy/k8s/install.ps1 -ImageTag sha-<commit>     con las imágenes que publicó CI en GHCR
#   powershell -File deploy/k8s/install.ps1 -Local                     con las construidas en la máquina (compose.app.yaml build)
param(
    [string]$ImageTag,
    [switch]$Local
)

$ErrorActionPreference = 'Stop'
if (-not $Local -and -not $ImageTag) { throw 'Indica -ImageTag sha-<commit> o -Local.' }

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
Set-Location $repo
$env:KUBECONFIG = "$repo\deploy\k8s\.kube\kubeconfig.yaml"
$helm = "$PSScriptRoot\helm.ps1"

function Assert-Ok([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Fallo: $step" }
}

Write-Output '--- Namespace y secretos'
kubectl apply -f deploy/k8s/infrastructure/namespace.yaml; Assert-Ok 'namespace'
& "$PSScriptRoot\create-secrets.ps1"; Assert-Ok 'secretos'

Write-Output '--- SQL Server y Keycloak (Kustomize)'
kubectl kustomize --load-restrictor LoadRestrictionsNone deploy/k8s/infrastructure | kubectl apply -f -; Assert-Ok 'infraestructura'
kubectl -n helpdesk rollout status statefulset/sqlserver --timeout=20m; Assert-Ok 'SQL Server'
kubectl -n helpdesk rollout status deployment/keycloak --timeout=10m; Assert-Ok 'Keycloak'

Write-Output '--- Redis (chart de terceros)'
powershell -NoProfile -File $helm upgrade --install redis valkey --repo https://valkey.io/valkey-helm/ --version 0.12.1 `
    -n helpdesk -f /values/redis-values.yaml --wait --timeout 5m; Assert-Ok 'Redis'

if ($Local) {
    Write-Output '--- Importar las imagenes locales al cluster'
    # Con cmd: la tubería de PowerShell 5.1 corrompe los datos binarios.
    cmd /c 'docker save helpdesk-api:local helpdesk-bff:local helpdesk-admin:local helpdesk-migrator:local | docker exec -i helpdesk-k8s-k3s-1 ctr -n k8s.io images import -'
    Assert-Ok 'importar imagenes'

    Write-Output '--- HelpDesk (nuestro chart, con imagenes locales)'
    powershell -NoProfile -File $helm upgrade --install helpdesk /charts/helpdesk -n helpdesk -f /values/values.local.yaml --wait --timeout 8m
    Assert-Ok 'HelpDesk'

    # El tag "local" no cambia entre versiones: sin esto, Kubernetes no se entera de que hay una imagen nueva.
    kubectl -n helpdesk rollout restart deployment/api deployment/bff deployment/admin; Assert-Ok 'reiniciar'
    foreach ($app in 'api', 'bff', 'admin') {
        kubectl -n helpdesk rollout status "deployment/$app" --timeout=5m; Assert-Ok $app
    }
}
else {
    Write-Output "--- HelpDesk (nuestro chart, $ImageTag)"
    powershell -NoProfile -File $helm upgrade --install helpdesk /charts/helpdesk -n helpdesk --set "image.tag=$ImageTag" --wait --timeout 8m
}
Assert-Ok 'HelpDesk'

kubectl -n helpdesk get pods
Write-Output 'Portal: https://portal.localhost:8443   Panel: https://panel.localhost:8443   (certificado autofirmado de Traefik)'
