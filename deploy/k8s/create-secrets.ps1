# Crea o actualiza el Secret de la aplicación a partir de deploy/.env. Los valores van por la entrada estándar de kubectl: no quedan en
# ningún archivo del repositorio, ni en la línea de comandos, donde cualquier proceso podría leerlos. Tampoco se muestran.
#
# Un Secret de Kubernetes solo está en Base64, y quien pueda leerlo en el clúster ve el valor. En producción, los secretos vienen de un
# almacén como Key Vault (External Secrets Operator o el CSI driver).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

function Read-EnvValue([string]$name) {
    $line = Get-Content "$repo\deploy\.env" | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
    if (-not $line) { throw "Falta $name en deploy/.env" }
    ($line -split '=', 2)[1].Trim("'")
}

$saPassword = Read-EnvValue 'MSSQL_SA_PASSWORD'
$redisPassword = Read-EnvValue 'HELPDESK_REDIS_PASSWORD'
$rabbitMqPassword = Read-EnvValue 'RABBITMQ_PASSWORD'

$secret = @{
    apiVersion = 'v1'
    kind       = 'Secret'
    metadata   = @{ name = 'helpdesk-secrets'; namespace = 'helpdesk' }
    type       = 'Opaque'
    stringData = @{
        'sa-password'             = $saPassword
        'connection-string'       = "Server=sqlserver,1433;Database=HelpDesk;User Id=sa;Password=$saPassword;TrustServerCertificate=True"
        'redis-password'          = $redisPassword
        'redis-connection'        = "redis:6379,password=$redisPassword"
        'rabbitmq-password'       = $rabbitMqPassword
        'rabbitmq-connection'     = "amqp://helpdesk:$rabbitMqPassword@rabbitmq:5672"
        'jwt-signing-key'         = Read-EnvValue 'HELPDESK_JWT_SIGNING_KEY'
        'bff-client-secret'       = Read-EnvValue 'HELPDESK_BFF_CLIENT_SECRET'
        'admin-client-secret'     = Read-EnvValue 'HELPDESK_ADMIN_CLIENT_SECRET'
        'keycloak-admin-password' = Read-EnvValue 'KEYCLOAK_ADMIN_PASSWORD'
        'demo-password'           = Read-EnvValue 'HELPDESK_DEV_PASSWORD'
    }
}

$OutputEncoding = [Text.UTF8Encoding]::new($false)
$secret | ConvertTo-Json -Depth 4 -Compress | kubectl apply -f -
