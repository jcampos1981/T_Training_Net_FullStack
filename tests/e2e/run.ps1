# Corre las pruebas de punta a punta. Ver README.md.
#   powershell -File tests/e2e/run.ps1                        todas, menos las largas
#   powershell -File tests/e2e/run.ps1 -Suite health,cache    solo esas
#   powershell -File tests/e2e/run.ps1 -Long                  ademas espera el vencimiento real del token (5 min por suite)
#   powershell -File tests/e2e/run.ps1 -Suite containers -NoBuild    la aplicacion en contenedores (no entra en "todas")
#   powershell -File tests/e2e/run.ps1 -Suite kubernetes -NoBuild    la aplicacion en el cluster de deploy/k8s, ya instalada (tampoco)
param(
    [string[]]$Suite = @('all'),
    [switch]$Long,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$root = $PSScriptRoot
$suites = Join-Path $root 'suites'
$shots = Join-Path $root 'shots'
New-Item -ItemType Directory -Force $shots | Out-Null

# Los secretos salen de los mismos lugares que usa la aplicacion, y nunca se muestran.
$userSecrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\d0995617-ac24-4e5d-97fc-fee84e147509\secrets.json" -Raw | ConvertFrom-Json
function Read-EnvValue([string]$name) {
    ((Get-Content "$repo\deploy\.env") | Where-Object { $_ -like "$name=*" }) -replace "^$name='?([^']*)'?$", '$1'
}
$env:HELPDESK_DEV_PASSWORD = $userSecrets.'DevelopmentUsers:Password'
$env:KEYCLOAK_ADMIN_PASSWORD = Read-EnvValue 'KEYCLOAK_ADMIN_PASSWORD'
$env:MSSQL_SA = Read-EnvValue 'MSSQL_SA_PASSWORD'

function Test-PortBusy([int]$port) {
    [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

function Stop-HelpDesk {
    Get-Process HelpDesk.Api, HelpDesk.Bff, HelpDesk.Admin -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Wait-Url([string]$url) {
    for ($i = 0; $i -lt 120; $i++) {
        if ((curl.exe -sk -o NUL -w '%{http_code}' $url) -eq '200') { return }
        Start-Sleep -Milliseconds 500
    }
    throw "No respondio: $url"
}

function Start-Exe([string]$project, [string]$url, [hashtable]$environment = @{}) {
    $saved = @{}
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $url
    foreach ($key in $environment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $environment[$key])
    }
    Start-Process "$repo\src\$project\bin\Debug\net10.0\$project.exe" -WorkingDirectory "$repo\src\$project" -WindowStyle Hidden `
        -RedirectStandardOutput "$shots\$project.log" -RedirectStandardError "$shots\$project.err.log" | Out-Null
    Remove-Item Env:\ASPNETCORE_URLS
    foreach ($key in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $saved[$key])
    }
}

function Start-Api([hashtable]$environment = @{}) {
    Start-Exe 'HelpDesk.Api' 'http://localhost:5159' $environment
    Wait-Url 'http://localhost:5159/health/live'
}

function Start-Bff {
    Start-Exe 'HelpDesk.Bff' 'https://localhost:7200'
    Wait-Url 'https://localhost:7200/health/live'
}

function Start-Vite([int]$port) {
    $process = Start-Process cmd -ArgumentList "/c npm run dev -- --port $port" -WorkingDirectory "$repo\src\HelpDesk.Portal" -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$shots\vite.log" -RedirectStandardError "$shots\vite.err.log"
    Wait-Url "http://localhost:$port/"
    $process
}

$longArgument = if ($Long) { @('largo') } else { @() }

# Cada suite arranca con la API limpia: el limite de intentos de login vive en su memoria.
$definitions = [ordered]@{
    'api-keycloak' = { Start-Api; powershell -NoProfile -ExecutionPolicy Bypass -File "$suites\api-keycloak.ps1" }
    'api-portal'   = { Start-Api; powershell -NoProfile -ExecutionPolicy Bypass -File "$suites\api-portal.ps1" }
    'api-bff'      = { Start-Api; powershell -NoProfile -ExecutionPolicy Bypass -File "$suites\api-bff.ps1" }
    'api-cors'     = { Start-Api; powershell -NoProfile -ExecutionPolicy Bypass -File "$suites\api-cors-ratelimit.ps1" }
    'hub'          = { Start-Api @{ Jwt__AccessTokenMinutes = '1' }; node "$suites\hub.mjs" $repo }
    'bff-outage'   = { Start-Api; node "$suites\bff-outage.mjs" $root $repo }
    'sla'          = { node "$suites\sla.mjs" $root $shots $repo }
    'cache'        = { node "$suites\cache.mjs" $repo }
    'redis-clients'= { node "$suites\redis-clients.mjs" $root $shots $repo }
    'health'       = {
        $env:NODE_TLS_REJECT_UNAUTHORIZED = '0'  # el certificado de desarrollo; este proceso solo habla con localhost
        $env:NODE_NO_WARNINGS = '1'
        node "$suites\health.mjs" $repo
        Remove-Item Env:\NODE_TLS_REJECT_UNAUTHORIZED, Env:\NODE_NO_WARNINGS
    }
    'admin-b4'     = { Start-Api; node "$suites\admin-b4.mjs" $root $shots $repo }
    'admin-b5'     = { Start-Api; node "$suites\admin-b5.mjs" $root $shots $repo }
    'admin-live'   = { node "$suites\admin-live.mjs" $root $shots $repo @longArgument }
    'portal-b3'    = { Start-Api; Start-Bff; node "$suites\portal-b3.mjs" $root $shots 'https://localhost:7200' }
    'portal-live'  = { Start-Api; node "$suites\portal-live.mjs" $root $shots $repo 'https://localhost:7200' @longArgument }
    # Vite en otro puerto: el 5173 puede estar en uso por otro proyecto, y estas pruebas no deben abrir otra aplicacion.
    'portal-vite'  = {
        Start-Api
        $vite = Start-Vite 5174
        try { node "$suites\portal-live.mjs" $root $shots $repo 'http://localhost:5174' }
        finally { taskkill /PID $vite.Id /T /F 2>$null | Out-Null }
    }
}

# Keycloak solo acepta el puerto 5173 de Vite: estas dos necesitan ese puerto libre.
$needsPort5173 = [ordered]@{
    'portal-b2'      = { Start-Api; Start-Bff; $vite = Start-Vite 5173; try { node "$suites\portal-b2.mjs" $root $shots } finally { taskkill /PID $vite.Id /T /F 2>$null | Out-Null } }
    'portal-b3-vite' = { Start-Api; Start-Bff; $vite = Start-Vite 5173; try { node "$suites\portal-b3.mjs" $root $shots 'http://localhost:5173' } finally { taskkill /PID $vite.Id /T /F 2>$null | Out-Null } }
}
foreach ($name in $needsPort5173.Keys) { $definitions[$name] = $needsPort5173[$name] }

# Las imagenes de produccion, con docker compose. Fuera de "todas": construirlas tarda varios minutos la primera vez, y no usa
# lo que compilo dotnet build.
$containers = {
    $compose = @('compose', '-f', "$repo\deploy\compose.yaml", '-f', "$repo\deploy\compose.app.yaml")
    docker @compose up -d --build --wait --wait-timeout 300 bff admin
    try { node "$suites\containers.mjs" $root $shots $repo }
    finally { docker @compose stop admin bff api }
}

# Con powershell -File, -Suite a,b llega como un solo texto.
$Suite = @($Suite -split ',')
$selected = if ($Suite -contains 'all') { @($definitions.Keys) } else { $Suite }
$definitions['containers'] = $containers
$definitions['kubernetes'] = { node "$suites\kubernetes.mjs" $root $shots $repo }
foreach ($name in $selected) {
    if (-not $definitions.Contains($name)) { throw "No existe la suite '$name'. Disponibles: $($definitions.Keys -join ', ')" }
}

if (-not (Test-Path "$root\node_modules")) {
    Push-Location $root
    npm ci
    Pop-Location
}

if (-not $NoBuild) {
    Set-Location $repo
    dotnet build HelpDesk.slnx -v q
    Push-Location src\HelpDesk.Portal
    npm run build | Out-Null
    Pop-Location
}

Set-Location $repo
$failed = 0
foreach ($name in $selected) {
    if ($needsPort5173.Contains($name) -and (Test-PortBusy 5173)) {
        Write-Output "SKIP  $name  (el puerto 5173 esta ocupado, y Keycloak solo acepta ese)"
        continue
    }

    Write-Output "===== $name ($(Get-Date -Format 'HH:mm:ss')) ====="
    Stop-HelpDesk
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $definitions[$name] 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        Stop-HelpDesk
        $ErrorActionPreference = 'Stop'
    }

    $output | Out-File -Encoding utf8 "$shots\$name.txt"
    $failures = @($output | Where-Object { $_ -match '^FAIL' })
    $failures | ForEach-Object { Write-Output $_ }
    $output | Where-Object { $_ -match '^(TOTAL|RESULTADO)' } | Select-Object -Last 1 | ForEach-Object { Write-Output $_ }
    if ($failures.Count -gt 0 -or -not ($output | Where-Object { $_ -match '^(TOTAL|RESULTADO)' })) { $failed++ }
}

Write-Output "Suites con problemas: $failed. Detalle en $shots"
exit $failed
