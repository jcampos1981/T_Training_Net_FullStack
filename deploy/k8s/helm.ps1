# Helm sin instalarlo: corre en un contenedor, con la configuración de kubectl del clúster local (deploy/k8s/cluster.yaml).
# Los argumentos son los de helm. Los charts del repositorio se ven en /charts, y un archivo de valores en /values:
#   powershell -File deploy/k8s/helm.ps1 list -A
#   powershell -File deploy/k8s/helm.ps1 upgrade --install helpdesk /charts/helpdesk -n helpdesk -f /values/values.local.yaml
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

# La red del contenedor de k3s: así 127.0.0.1:6443, la dirección que trae kubeconfig.yaml, es su API.
docker run --rm `
    --network container:helpdesk-k8s-k3s-1 `
    -v "$repo\deploy\k8s\.kube\kubeconfig.yaml:/root/.kube/config:ro" `
    -v "$repo\deploy\helm:/charts:ro" `
    -v "$repo\deploy\k8s:/values:ro" `
    -v helpdesk-helm-cache:/root/.cache/helm `
    -v helpdesk-helm-config:/root/.config/helm `
    alpine/helm:4.3.1 @args
exit $LASTEXITCODE
