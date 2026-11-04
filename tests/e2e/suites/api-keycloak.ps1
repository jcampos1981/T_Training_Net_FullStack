param(
    [string]$Api = 'http://localhost:5159',
    [string]$Realm = 'http://localhost:8080/realms/helpdesk',
    [string]$ClientId = 'helpdesk-scalar'
)

Add-Type -AssemblyName System.Net.Http
$ErrorActionPreference = 'Stop'

$secrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\d0995617-ac24-4e5d-97fc-fee84e147509\secrets.json" -Raw | ConvertFrom-Json
$password = $secrets.'DevelopmentUsers:Password'

$script:pass = 0
$script:fail = 0
function Check([string]$name, $ok, [string]$detail = '') {
    if ([bool]$ok) { $script:pass++; Write-Output ("PASS  {0}  {1}" -f $name, $detail) }
    else { $script:fail++; Write-Output ("FAIL  {0}  {1}" -f $name, $detail) }
}

function ConvertTo-Base64Url([byte[]]$bytes) { [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function ConvertFrom-Base64Url([string]$text) {
    $t = $text.Replace('-', '+').Replace('_', '/')
    switch ($t.Length % 4) { 2 { $t += '==' } 3 { $t += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($t))
}
function Get-Payload([string]$jwt) { ConvertFrom-Base64Url ($jwt.Split('.')[1]) | ConvertFrom-Json }

function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    New-Object System.Net.Http.HttpClient($handler)
}

function New-Form([hashtable]$values) {
    $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
    foreach ($key in $values.Keys) { $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' -ArgumentList $key, ([string]$values[$key]))) }
    [System.Net.Http.FormUrlEncodedContent]::new($pairs)
}

# Lo que hace el navegador: pide la pagina de login (con code_challenge), envia usuario y contrasena,
# y recibe un 302 hacia redirect_uri con ?code=. Devuelve el codigo y el code_verifier para canjearlo.
function Get-AuthorizationCode([string]$user, [string]$redirectUri = "$Api/scalar/", [switch]$NoPkce) {
    # Keycloak marca sus cookies "Secure" aun en http. Los navegadores las mandan igual a localhost (lo tratan como
    # origen seguro); el CookieContainer de .NET no, asi que aqui se copian a mano.
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $random = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
    $verifier = ConvertTo-Base64Url $random
    $challenge = ConvertTo-Base64Url ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))
    $state = [guid]::NewGuid().ToString('N')

    $query = "client_id=$ClientId&response_type=code&scope=openid%20profile%20email&state=$state&redirect_uri=" + [Uri]::EscapeDataString($redirectUri)
    if (-not $NoPkce) { $query += "&code_challenge=$challenge&code_challenge_method=S256" }

    $page = $client.GetAsync("$Realm/protocol/openid-connect/auth?$query").Result
    $html = $page.Content.ReadAsStringAsync().Result
    $location = if ($page.Headers.Location) { $page.Headers.Location.ToString() } else { '' }
    if ([int]$page.StatusCode -ne 200) { return [pscustomobject]@{ Error = "auth $([int]$page.StatusCode) $location"; Html = $html } }

    $formTag = [regex]::Match($html, '<form[^>]*kc-form-login[^>]*>').Value
    $action = [Net.WebUtility]::HtmlDecode([regex]::Match($formTag, 'action="([^"]+)"').Groups[1].Value)
    if (-not $action) { return [pscustomobject]@{ Error = 'no login form'; Html = $html } }

    $cookies = ($page.Headers | Where-Object { $_.Key -eq 'Set-Cookie' }).Value | ForEach-Object { $_.Split(';')[0] }
    $post = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $action)
    $post.Headers.TryAddWithoutValidation('Cookie', ($cookies -join '; ')) | Out-Null
    $post.Content = New-Form @{ username = $user; password = $password; credentialId = '' }
    $login = $client.SendAsync($post).Result
    if ([int]$login.StatusCode -ne 302) { return [pscustomobject]@{ Error = "login $([int]$login.StatusCode)"; Html = $login.Content.ReadAsStringAsync().Result } }

    $location = $login.Headers.Location.ToString()
    $code = [Uri]::UnescapeDataString([regex]::Match($location, '[?&]code=([^&]+)').Groups[1].Value)
    $returnedState = [regex]::Match($location, '[?&]state=([^&]+)').Groups[1].Value
    [pscustomobject]@{ Error = $null; Code = $code; Verifier = $verifier; RedirectUri = $redirectUri; Location = $location; StateOk = ($returnedState -eq $state); Client = $client }
}

function Invoke-TokenEndpoint([hashtable]$form, [string]$origin) {
    $client = New-Client
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Realm/protocol/openid-connect/token")
    if ($origin) { $request.Headers.Add('Origin', $origin) }
    $request.Content = New-Form $form
    $response = $client.SendAsync($request).Result
    $allowOrigin = ($response.Headers | Where-Object { $_.Key -eq 'Access-Control-Allow-Origin' }).Value -join ','
    [pscustomobject]@{ Status = [int]$response.StatusCode; Body = ($response.Content.ReadAsStringAsync().Result | ConvertFrom-Json); AllowOrigin = $allowOrigin }
}

function Redeem($auth, [string]$verifier = $auth.Verifier, [string]$origin) {
    $form = @{ grant_type = 'authorization_code'; code = $auth.Code; redirect_uri = $auth.RedirectUri; client_id = $ClientId }
    if ($verifier) { $form.code_verifier = $verifier }
    Invoke-TokenEndpoint $form $origin
}

function Get-KeycloakTokens([string]$user) {
    $auth = Get-AuthorizationCode $user
    if ($auth.Error) { throw "login $user fallo: $($auth.Error)" }
    (Redeem $auth).Body
}

function Invoke-Api([string]$method, [string]$path, [string]$token) {
    $client = New-Client
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), "$Api$path")
    if ($token) { $request.Headers.TryAddWithoutValidation('Authorization', "Bearer $token") | Out-Null }
    $response = $client.SendAsync($request).Result
    $text = $response.Content.ReadAsStringAsync().Result
    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
    $challenge = ($response.Headers.WwwAuthenticate | ForEach-Object { $_.ToString() }) -join ' '
    [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json; Text = $text; Challenge = $challenge }
}

Write-Output '--- 1. Authorization Code + PKCE (lo que hace Scalar o un SPA) ---'
$auth = Get-AuthorizationCode 'pedro'
Check 'login pedro devuelve un codigo' (-not $auth.Error -and $auth.Code) $auth.Error
Check 'state vuelve igual (anti-CSRF)' $auth.StateOk
$redeemed = Redeem $auth
Check 'canje del codigo con code_verifier' ($redeemed.Status -eq 200) "status=$($redeemed.Status)"
$pedro = $redeemed.Body
$claims = Get-Payload $pedro.access_token
Write-Output ("      iss={0} aud={1} azp={2} user={3} roles={4} vida={5}s" -f $claims.iss, ($claims.aud -join ','), $claims.azp, $claims.preferred_username, ($claims.realm_access.roles -join ','), ($claims.exp - $claims.iat))
$header = ConvertFrom-Base64Url ($pedro.access_token.Split('.')[0]) | ConvertFrom-Json
Check 'access token firmado con RS256' ($header.alg -eq 'RS256') "kid=$($header.kid)"
Check 'aud incluye helpdesk-api (audience mapper)' (@($claims.aud) -contains 'helpdesk-api') ($claims.aud -join ',')

$again = Redeem $auth
Check 'el mismo codigo por segunda vez: rechazado' ($again.Status -eq 400) "$($again.Status) $($again.Body.error)"
$revoked = Invoke-TokenEndpoint @{ grant_type = 'refresh_token'; refresh_token = $pedro.refresh_token; client_id = $ClientId }
Check 'y Keycloak revoca la sesion que nacio de ese codigo' ($revoked.Status -eq 400) "$($revoked.Status) $($revoked.Body.error) $($revoked.Body.error_description)"
$stillPedro = Invoke-Api GET '/api/me' $pedro.access_token
Check 'el access token ya emitido sigue sirviendo en la API (no se puede revocar)' ($stillPedro.Status -eq 200) "status=$($stillPedro.Status)"

$auth2 = Get-AuthorizationCode 'pedro'
$wrongVerifier = Redeem $auth2 'otro-verifier-que-no-corresponde-al-challenge-xxxxxxx'
Check 'code_verifier equivocado: rechazado (PKCE)' ($wrongVerifier.Status -eq 400) "$($wrongVerifier.Status) $($wrongVerifier.Body.error_description)"

$noPkce = Get-AuthorizationCode 'pedro' -NoPkce
Check 'sin code_challenge: Keycloak no inicia el login' ([bool]$noPkce.Error) "$($noPkce.Error)"

$evil = Get-AuthorizationCode 'pedro' 'http://evil.example/scalar/'
Check 'redirect_uri no registrada: rechazada' ([bool]$evil.Error) "$($evil.Error)"

foreach ($uri in "$Api/scalar", "$Api/scalar/v1", 'https://localhost:7082/scalar/') {
    $variant = Get-AuthorizationCode 'pedro' $uri
    Check "redirect_uri $uri aceptada" (-not $variant.Error) "$($variant.Error)"
}

Write-Output '--- 2. La API con tokens de Keycloak ---'
$me = Invoke-Api GET '/api/me' $pedro.access_token
Check '/api/me pedro (Keycloak)' ($me.Status -eq 200 -and $me.Json.agent -eq 'pedro' -and (@($me.Json.roles) -join ',') -eq 'Agent' -and $me.Json.authenticatedWith -eq 'Keycloak') ($me.Text)

$list = Invoke-Api GET '/api/tickets?pageSize=5' $pedro.access_token
$owners = @($list.Json.items | ForEach-Object { $_.assignedTo } | Select-Object -Unique)
Check 'pedro lista solo lo suyo' ($list.Status -eq 200 -and $owners.Count -eq 1 -and $owners[0] -eq 'pedro') "status=$($list.Status) owners=$($owners -join ',')"

$other = Invoke-Api GET '/api/tickets?assignedTo=luis' $pedro.access_token
Check 'pedro pide los de luis: 403' ($other.Status -eq 403) "status=$($other.Status)"

$dash = Invoke-Api GET '/api/dashboard' $pedro.access_token
Check 'pedro en el dashboard: 403' ($dash.Status -eq 403) "status=$($dash.Status)"

$laura = Get-KeycloakTokens 'laura'
$meLaura = Invoke-Api GET '/api/me' $laura.access_token
Check '/api/me laura (Keycloak, Supervisor, sin agente)' ($meLaura.Status -eq 200 -and (@($meLaura.Json.roles) -join ',') -eq 'Supervisor' -and -not $meLaura.Json.agent) ($meLaura.Text)
$dashLaura = Invoke-Api GET '/api/dashboard' $laura.access_token
Check 'laura en el dashboard: 200' ($dashLaura.Status -eq 200) "status=$($dashLaura.Status)"
$luisLaura = Invoke-Api GET '/api/tickets?assignedTo=luis&pageSize=2' $laura.access_token
Check 'laura ve los de luis: 200' ($luisLaura.Status -eq 200) "status=$($luisLaura.Status)"

Write-Output '--- 3. Tokens que la API debe rechazar ---'
$idToken = Invoke-Api GET '/api/me' $pedro.id_token
Check 'ID token como bearer: 401 (su aud es el client, no la API)' ($idToken.Status -eq 401) "status=$($idToken.Status) aud=$((Get-Payload $pedro.id_token).aud)"

$parts = $pedro.access_token.Split('.')
$forged = (Get-Payload $pedro.access_token)
$forged.realm_access.roles = @('Supervisor')
$forgedPayload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes(($forged | ConvertTo-Json -Depth 10 -Compress)))
$tampered = Invoke-Api GET '/api/dashboard' ("{0}.{1}.{2}" -f $parts[0], $forgedPayload, $parts[2])
Check 'payload alterado (se pone Supervisor): 401' ($tampered.Status -eq 401) "status=$($tampered.Status)"

$noneHeader = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"none","typ":"JWT"}'))
$none = Invoke-Api GET '/api/me' ("{0}.{1}." -f $noneHeader, $parts[1])
Check 'alg none (sin firma): 401' ($none.Status -eq 401) "status=$($none.Status)"

$malformed = Invoke-Api GET '/api/me' 'aaa.bbb.ccc'
Check 'JWT ilegible (aaa.bbb.ccc): 401, no 500' ($malformed.Status -eq 401) "status=$($malformed.Status)"

Write-Output '--- 4. Las otras credenciales siguen funcionando ---'
$client = New-Client
$ownJwt = ($client.PostAsync("$Api/api/token", [System.Net.Http.StringContent]::new((@{ email = 'pedro@helpdesk.local'; password = $password } | ConvertTo-Json), [Text.Encoding]::UTF8, 'application/json')).Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json)
$meOwn = Invoke-Api GET '/api/me' $ownJwt.accessToken
Check 'JWT propio (/api/token): authenticatedWith Bearer' ($meOwn.Status -eq 200 -and $meOwn.Json.authenticatedWith -eq 'Bearer') ($meOwn.Text)

$identity = ($client.PostAsync("$Api/api/auth/login", [System.Net.Http.StringContent]::new((@{ email = 'pedro@helpdesk.local'; password = $password } | ConvertTo-Json), [Text.Encoding]::UTF8, 'application/json')).Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json)
$meIdentity = Invoke-Api GET '/api/me' $identity.accessToken
Check 'token de Identity: authenticatedWith Identity.Bearer...' ($meIdentity.Status -eq 200 -and $meIdentity.Json.authenticatedWith -like 'Identity.Bearer*') ($meIdentity.Text)

$anonymous = Invoke-Api GET '/api/tickets' $null
Check 'sin credencial: 401' ($anonymous.Status -eq 401) "status=$($anonymous.Status)"

Write-Output '--- 5. Refresh y logout en Keycloak ---'
$fresh = Get-KeycloakTokens 'pedro'
$refreshed = Invoke-TokenEndpoint @{ grant_type = 'refresh_token'; refresh_token = $fresh.refresh_token; client_id = $ClientId }
Check 'refresh token de Keycloak: nuevo access token' ($refreshed.Status -eq 200 -and $refreshed.Body.access_token -ne $pedro.access_token) "status=$($refreshed.Status)"

$logoutClient = New-Client
$logout = $logoutClient.PostAsync("$Realm/protocol/openid-connect/logout", (New-Form @{ client_id = $ClientId; refresh_token = $refreshed.Body.refresh_token })).Result
Check 'logout en Keycloak (cierra la sesion)' ([int]$logout.StatusCode -eq 204) "status=$([int]$logout.StatusCode)"
$afterLogout = Invoke-TokenEndpoint @{ grant_type = 'refresh_token'; refresh_token = $refreshed.Body.refresh_token; client_id = $ClientId }
Check 'despues del logout, el refresh token ya no sirve' ($afterLogout.Status -eq 400) "$($afterLogout.Status) $($afterLogout.Body.error)"
$stillValid = Invoke-Api GET '/api/me' $refreshed.Body.access_token
Check 'pero el access token sigue valido en la API hasta que vence' ($stillValid.Status -eq 200) "status=$($stillValid.Status)"

Write-Output '--- 6. OpenAPI, Scalar y CORS de Keycloak ---'
$doc = Invoke-Api GET '/openapi/v1.json' $null
$scheme = $doc.Json.components.securitySchemes.Keycloak
Check 'OpenAPI: esquema oauth2 Keycloak' ($scheme.type -eq 'oauth2' -and $scheme.flows.authorizationCode.tokenUrl -eq "$Realm/protocol/openid-connect/token") ($scheme | ConvertTo-Json -Depth 5 -Compress)
Check 'OpenAPI: Bearer y Keycloak como alternativas' (@($doc.Json.security).Count -eq 2) (($doc.Json.security | ConvertTo-Json -Compress))
$scalar = Invoke-Api GET '/scalar/' $null
Check 'Scalar trae el clientId y PKCE' ($scalar.Status -eq 200 -and $scalar.Text -match 'helpdesk-scalar' -and $scalar.Text -match 'SHA-256') "status=$($scalar.Status)"

$corsOk = Redeem (Get-AuthorizationCode 'pedro') -origin $Api
Check "CORS: el canje desde $Api trae Access-Control-Allow-Origin" ($corsOk.Status -eq 200 -and $corsOk.AllowOrigin -eq $Api) "status=$($corsOk.Status) allow-origin=$($corsOk.AllowOrigin)"
$corsEvil = Redeem (Get-AuthorizationCode 'pedro') -origin 'http://evil.example'
Check 'CORS: desde otro origen, sin Access-Control-Allow-Origin (el navegador no deja leer)' (-not $corsEvil.AllowOrigin) "status=$($corsEvil.Status) allow-origin=$($corsEvil.AllowOrigin)"

Write-Output ("RESULTADO: {0} PASS, {1} FAIL" -f $script:pass, $script:fail)
