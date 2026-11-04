param([string]$Api = 'http://localhost:5159', [string]$Spa = 'http://localhost:5173')

Add-Type -AssemblyName System.Net.Http
$ErrorActionPreference = 'Stop'
$secrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\d0995617-ac24-4e5d-97fc-fee84e147509\secrets.json" -Raw | ConvertFrom-Json
$password = $secrets.'DevelopmentUsers:Password'

$script:pass = 0; $script:fail = 0
function Check([string]$name, $ok, [string]$detail = '') {
    if ([bool]$ok) { $script:pass++; Write-Output ("PASS  {0}  {1}" -f $name, $detail) }
    else { $script:fail++; Write-Output ("FAIL  {0}  {1}" -f $name, $detail) }
}

$client = New-Object System.Net.Http.HttpClient
function Send([string]$method, [string]$path, [hashtable]$headers, [string]$json) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), "$Api$path")
    if ($headers) { foreach ($h in $headers.Keys) { $request.Headers.TryAddWithoutValidation($h, [string]$headers[$h]) | Out-Null } }
    if ($json) { $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json') }
    $response = $client.SendAsync($request).Result
    $all = @{}
    foreach ($h in $response.Headers) { $all[$h.Key] = ($h.Value -join ', ') }
    foreach ($h in $response.Content.Headers) { $all[$h.Key] = ($h.Value -join ', ') }
    $text = $response.Content.ReadAsStringAsync().Result
    $body = $null; if ($text) { try { $body = $text | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $all; Json = $body }
}
function Get-Token([string]$email) {
    (Send POST '/api/token' $null (@{ email = $email; password = $password } | ConvertTo-Json)).Json.accessToken
}

$pedro = Get-Token 'pedro@helpdesk.local'
$laura = Get-Token 'laura@helpdesk.local'

Write-Output '--- 1. CORS ---'
$pre = Send OPTIONS '/api/tickets' @{ Origin = $Spa; 'Access-Control-Request-Method' = 'GET'; 'Access-Control-Request-Headers' = 'authorization' }
Check "preflight desde $Spa (sin token): 204, no 401" ($pre.Status -eq 204 -and $pre.Headers['Access-Control-Allow-Origin'] -eq $Spa) ("status={0} allow-origin={1} allow-headers={2} max-age={3}" -f $pre.Status, $pre.Headers['Access-Control-Allow-Origin'], $pre.Headers['Access-Control-Allow-Headers'], $pre.Headers['Access-Control-Max-Age'])
$preEvil = Send OPTIONS '/api/tickets' @{ Origin = 'http://evil.example'; 'Access-Control-Request-Method' = 'GET'; 'Access-Control-Request-Headers' = 'authorization' }
Check 'preflight desde otro origen: sin Access-Control-Allow-Origin' (-not $preEvil.Headers['Access-Control-Allow-Origin']) "status=$($preEvil.Status)"
$prePatch = Send OPTIONS '/api/tickets/1' @{ Origin = $Spa; 'Access-Control-Request-Method' = 'PATCH' }
Check 'preflight con un metodo no permitido (PATCH): sin permiso' (-not $prePatch.Headers['Access-Control-Allow-Methods'] -or $prePatch.Headers['Access-Control-Allow-Methods'] -notmatch 'PATCH') "allow-methods=$($prePatch.Headers['Access-Control-Allow-Methods'])"

$get = Send GET '/api/me' @{ Origin = $Spa; Authorization = "Bearer $pedro" }
Check "GET desde $Spa con token: 200 + Allow-Origin + Expose-Headers" ($get.Status -eq 200 -and $get.Headers['Access-Control-Allow-Origin'] -eq $Spa -and $get.Headers['Access-Control-Expose-Headers'] -match 'Retry-After') ("expose={0}" -f $get.Headers['Access-Control-Expose-Headers'])
$getEvil = Send GET '/api/me' @{ Origin = 'http://evil.example'; Authorization = "Bearer $pedro" }
Check 'GET desde otro origen con token: la API RESPONDE 200, solo falta Allow-Origin (CORS no es autorizacion)' ($getEvil.Status -eq 200 -and -not $getEvil.Headers['Access-Control-Allow-Origin']) "status=$($getEvil.Status)"
$get401 = Send GET '/api/me' @{ Origin = $Spa }
Check '401 tambien lleva Allow-Origin (el frontend puede leer que falta sesion)' ($get401.Status -eq 401 -and $get401.Headers['Access-Control-Allow-Origin'] -eq $Spa) "status=$($get401.Status)"

Write-Output '--- 2. Rate limiting de login (10 por minuto por IP; ya se usaron 2) ---'
$statuses = @()
$rejected = $null
for ($i = 1; $i -le 10; $i++) {
    $r = Send POST '/api/token' $null (@{ email = 'nadie@helpdesk.local'; password = 'no-es-la-clave-123' } | ConvertTo-Json)
    $statuses += $r.Status
    if ($r.Status -eq 429 -and -not $rejected) { $rejected = $r }
}
Check 'intentos 3 a 10: 401; el 11 y 12: 429' ((($statuses[0..7] | Where-Object { $_ -eq 401 }).Count -eq 8) -and $statuses[8] -eq 429 -and $statuses[9] -eq 429) ($statuses -join ',')
Check '429 con Retry-After y ProblemDetails' ($rejected.Headers['Retry-After'] -match '^\d+$' -and $rejected.Json.title -eq 'Demasiadas peticiones' -and $rejected.Json.status -eq 429) ("retry-after={0}s title={1}" -f $rejected.Headers['Retry-After'], $rejected.Json.title)
$identityLogin = Send POST '/api/auth/login' $null (@{ email = 'nadie@helpdesk.local'; password = 'x' } | ConvertTo-Json)
Check '/api/auth/login comparte el mismo limite por IP: 429' ($identityLogin.Status -eq 429) "status=$($identityLogin.Status)"
$meAfterLogin = Send GET '/api/me' @{ Authorization = "Bearer $pedro" }
Check 'el resto de la API sigue respondiendo a pedro' ($meAfterLogin.Status -eq 200) "status=$($meAfterLogin.Status)"

Write-Output '--- 3. Rate limiting por usuario (cubeta de 60, se reponen 20 cada 10 s) ---'
$ok = 0; $first429 = $null; $sent = 0
while ($sent -lt 120 -and -not $first429) {
    $sent++
    $r = Send GET '/api/me' @{ Authorization = "Bearer $pedro"; Origin = $Spa }
    if ($r.Status -eq 200) { $ok++ } elseif ($r.Status -eq 429) { $first429 = $r }
}
Check 'pedro: unas 60 peticiones seguidas pasan, luego 429' ($first429 -and $ok -ge 50 -and $ok -le 80) "200 seguidos=$ok, 429 en la peticion $sent"
Check '429 por CORS: Allow-Origin y Retry-After visibles para el frontend' ($first429.Headers['Access-Control-Allow-Origin'] -eq $Spa -and $first429.Headers['Retry-After'] -match '^\d+$') ("retry-after={0}s" -f $first429.Headers['Retry-After'])
$lauraMe = Send GET '/api/me' @{ Authorization = "Bearer $laura" }
Check 'laura tiene su propia cubeta: 200' ($lauraMe.Status -eq 200) "status=$($lauraMe.Status)"
$wait = [int]$first429.Headers['Retry-After']
Start-Sleep -Seconds ($wait + 1)
$recovered = Send GET '/api/me' @{ Authorization = "Bearer $pedro" }
Check "tras esperar Retry-After ($wait s), pedro vuelve a pasar" ($recovered.Status -eq 200) "status=$($recovered.Status)"

Write-Output ("RESULTADO: {0} PASS, {1} FAIL" -f $script:pass, $script:fail)
