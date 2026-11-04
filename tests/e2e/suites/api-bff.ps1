param(
    [string]$Bff = 'https://localhost:7200',
    [string]$Api = 'http://localhost:5159',
    [string]$Keycloak = 'http://localhost:8080',
    [string]$Repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path,
    [string]$Scratch = (Join-Path $PSScriptRoot '..\shots')
)

Add-Type -AssemblyName System.Net.Http
$ErrorActionPreference = 'Stop'
# El certificado de desarrollo no es de confianza en este equipo: se acepta solo para localhost, solo en esta prueba.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (-not ('TrustLocalhost' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class TrustLocalhost {
    public static void Enable() { ServicePointManager.ServerCertificateValidationCallback = Validate; }
    private static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
        if (errors == SslPolicyErrors.None) return true;
        HttpWebRequest request = sender as HttpWebRequest;
        return request != null && request.RequestUri.Host == "localhost";
    }
}
'@
}
[TrustLocalhost]::Enable()

$secrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\d0995617-ac24-4e5d-97fc-fee84e147509\secrets.json" -Raw | ConvertFrom-Json
$password = $secrets.'DevelopmentUsers:Password'
$adminPassword = ((Get-Content "$Repo\deploy\.env") | Where-Object { $_ -like 'KEYCLOAK_ADMIN_PASSWORD=*' }) -replace "^KEYCLOAK_ADMIN_PASSWORD='?([^']*)'?$", '$1'

$script:pass = 0; $script:fail = 0
function Check([string]$name, $ok, [string]$detail = '') {
    if ([bool]$ok) { $script:pass++; Write-Output ("PASS  {0}  {1}" -f $name, $detail) }
    else { $script:fail++; Write-Output ("FAIL  {0}  {1}" -f $name, $detail) }
}

# ---------- Un "navegador" minimo: cookies por host:puerto, sin seguir redirecciones ----------
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$http = New-Object System.Net.Http.HttpClient($handler)

function New-Jar { @{} }
function Send($jar, [string]$method, [string]$url, [hashtable]$form, [hashtable]$headers) {
    $uri = [Uri]$url
    $key = "$($uri.Host):$($uri.Port)"
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), $uri)
    if ($jar[$key] -and $jar[$key].Count -gt 0) {
        $request.Headers.TryAddWithoutValidation('Cookie', (($jar[$key].GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ')) | Out-Null
    }
    if ($headers) { foreach ($h in $headers.Keys) { $request.Headers.TryAddWithoutValidation($h, [string]$headers[$h]) | Out-Null } }
    if ($form) {
        $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
        foreach ($k in $form.Keys) { $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' -ArgumentList $k, ([string]$form[$k]))) }
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    }
    $response = $http.SendAsync($request).Result
    $setCookies = @()
    $values = $null
    if ($response.Headers.TryGetValues('Set-Cookie', [ref]$values)) {
        if (-not $jar[$key]) { $jar[$key] = [ordered]@{} }
        foreach ($line in $values) {
            $setCookies += $line
            $parts = $line.Split(';')[0].Split([char[]]@('='), 2)
            if ($parts[1] -eq '' -or $line -match 'expires=Thu, 01 Jan 1970') { $jar[$key].Remove($parts[0]) } else { $jar[$key][$parts[0]] = $parts[1] }
        }
    }
    $location = $null
    if ($response.Headers.Location) { $location = (New-Object Uri($uri, $response.Headers.Location.ToString())).AbsoluteUri }
    $text = $response.Content.ReadAsStringAsync().Result
    $json = $null; if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Location = $location; Text = $text; Json = $json; SetCookies = $setCookies }
}

# Como el navegador: sigue las redirecciones mientras sigan dentro de Keycloak.
function Get-KeycloakPage($jar, [string]$url) {
    $page = Send $jar GET $url
    while ($page.Status -in 302, 303 -and $page.Location -like "$Keycloak*") { $page = Send $jar GET $page.Location }
    $page
}

# Login completo como lo haria el navegador: BFF -> Keycloak -> form_post de vuelta al BFF -> cookie de sesion.
function Login-Bff($jar, [string]$user, [string]$returnUrl = '/') {
    $start = Send $jar GET "$Bff/bff/login?returnUrl=$([Uri]::EscapeDataString($returnUrl))"
    $page = Get-KeycloakPage $jar $start.Location
    $formTag = [regex]::Match($page.Text, '<form[^>]*kc-form-login[^>]*>').Value
    $action = [Net.WebUtility]::HtmlDecode([regex]::Match($formTag, 'action="([^"]+)"').Groups[1].Value)
    if (-not $action) {
        $snippet = ($page.Text -replace '\s+', ' ')
        throw ("Login {0}: start={1} {2} page={3} location={4} body={5}" -f $user, $start.Status, $start.Location, $page.Status, $page.Location, $snippet.Substring(0, [Math]::Min(400, $snippet.Length)))
    }
    $posted = Send $jar POST $action @{ username = $user; password = $password; credentialId = '' }
    # response_mode=form_post: Keycloak devuelve una pagina con un formulario que se envia solo al BFF.
    $callbackAction = [Net.WebUtility]::HtmlDecode([regex]::Match($posted.Text, '(?i)<form[^>]*action="([^"]+)"').Groups[1].Value)
    if (-not $callbackAction) {
        $snippet = ($posted.Text -replace '\s+', ' ')
        throw ("Login {0}: page={1} action={2} posted={3} location={4} body={5}" -f $user, $page.Status, $action, $posted.Status, $posted.Location, $snippet.Substring(0, [Math]::Min(600, $snippet.Length)))
    }
    $fields = @{}
    foreach ($m in [regex]::Matches($posted.Text, '(?i)<input type="hidden" name="([^"]+)" value="([^"]*)"')) { $fields[$m.Groups[1].Value] = [Net.WebUtility]::HtmlDecode($m.Groups[2].Value) }
    $callback = Send $jar POST $callbackAction $fields
    [pscustomobject]@{ Start = $start; LoginStatus = $posted.Status; CallbackAction = $callbackAction; Fields = $fields.Keys; Callback = $callback }
}

$csrf = @{ 'X-CSRF' = '1' }

# ---------- El BFF lo levanta y lo reinicia este script ----------
$bffLog = Join-Path $Scratch 'bff.log'
function Start-Bff([string]$refreshBeforeExpiry) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $Bff
    $env:Keycloak__RefreshBeforeExpiry = $refreshBeforeExpiry
    $process = Start-Process -FilePath "$Repo\src\HelpDesk.Bff\bin\Debug\net10.0\HelpDesk.Bff.exe" -WorkingDirectory "$Repo\src\HelpDesk.Bff" -RedirectStandardOutput $bffLog -RedirectStandardError "$bffLog.err" -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(60)
    do { Start-Sleep -Milliseconds 500; $ready = $false; try { $ready = (Send (New-Jar) GET "$Bff/bff/user").Status -eq 401 } catch { } } while (-not $ready -and (Get-Date) -lt $deadline)
    if (-not $ready) { throw 'El BFF no arranco' }
    $process
}
function Get-RefreshCount { if (Test-Path $bffLog) { @(Select-String -Path $bffLog -Pattern 'Access token renovado').Count } else { 0 } }

$bffProcess = Start-Bff '00:01:00'
try {
    Write-Output '--- 1. Sin sesion ---'
    $anon = New-Jar
    $u = Send $anon GET "$Bff/bff/user"
    Check '/bff/user sin cookie: 401 (no 302 a un login)' ($u.Status -eq 401) "status=$($u.Status) location=$($u.Location)"
    $a = Send $anon GET "$Bff/api/me" $null $csrf
    Check '/api/me sin cookie: 401' ($a.Status -eq 401) "status=$($a.Status)"
    $homePage = Send $anon GET "$Bff/"
    Check 'el portal React (build) se sirve en /' ($homePage.Status -eq 200 -and $homePage.Text -match 'id="root"') "status=$($homePage.Status)"

    Write-Output '--- 2. Login de pedro (BFF -> Keycloak -> BFF) ---'
    $pedro = New-Jar
    $login = Login-Bff $pedro 'pedro'
    Check '/bff/login redirige a Keycloak' ($login.Start.Status -eq 302 -and $login.Start.Location -like "$Keycloak/realms/helpdesk/protocol/openid-connect/auth*") $login.Start.Location
    Check 'PAR: la URL solo lleva client_id y request_uri (sin code_challenge ni redirect_uri a la vista)' ($login.Start.Location -match 'request_uri=' -and $login.Start.Location -notmatch 'code_challenge=') ''
    Check 'Keycloak responde con form_post hacia /signin-oidc' ($login.LoginStatus -eq 200 -and $login.CallbackAction -eq "$Bff/signin-oidc") "fields=$($login.Fields -join ',')"
    Check 'el callback deja la sesion y vuelve a /' ($login.Callback.Status -eq 302 -and $login.Callback.Location -eq "$Bff/") "status=$($login.Callback.Status) location=$($login.Callback.Location)"
    $sessionCookie = @($login.Callback.SetCookies | Where-Object { $_ -like '__Host-helpdesk*' })
    $first = if ($sessionCookie.Count) { $sessionCookie[0].ToLowerInvariant() } else { '' }
    Check 'cookie __Host-helpdesk: secure, httponly, samesite=strict, path=/' ($first -match 'secure' -and $first -match 'httponly' -and $first -match 'samesite=strict' -and $first -match 'path=/') ("{0} cookie(s), {1} bytes en total" -f $sessionCookie.Count, (($sessionCookie | ForEach-Object { $_.Split(';')[0].Length }) | Measure-Object -Sum).Sum)

    $user = Send $pedro GET "$Bff/bff/user"
    Check '/bff/user: pedro con logoutUrl' ($user.Status -eq 200 -and $user.Json.name -eq 'pedro' -and $user.Json.logoutUrl -like '/bff/logout?sid=*') $user.Text
    $chunks = @($sessionCookie | ForEach-Object { $_.Split(';')[0].Split([char[]]@('='), 2)[1] })
    Check '/bff/user no trae tokens, y la cookie esta cifrada (Data Protection: CfDJ8...)' (-not ($user.Text -match 'eyJ') -and ($chunks -join '') -match '^chunks-\d+CfDJ8') ("{0}: {1}..." -f ($sessionCookie | ForEach-Object { $_.Split('=')[0] }) -join ',', ($chunks -join '').Substring(0, 20))

    Write-Output '--- 3. /api a traves del BFF ---'
    $noHeader = Send $pedro GET "$Bff/api/me"
    Check 'sin X-CSRF: 400' ($noHeader.Status -eq 400) "status=$($noHeader.Status) $($noHeader.Json.title)"
    $me = Send $pedro GET "$Bff/api/me" $null $csrf
    Check '/api/me via BFF: pedro, Agent, Keycloak' ($me.Status -eq 200 -and $me.Json.agent -eq 'pedro' -and $me.Json.authenticatedWith -eq 'Keycloak') $me.Text
    $tickets = Send $pedro GET "$Bff/api/tickets?pageSize=5" $null $csrf
    $owners = @($tickets.Json.items | ForEach-Object { $_.assignedTo } | Select-Object -Unique)
    Check '/api/tickets via BFF: solo los de pedro' ($tickets.Status -eq 200 -and $owners.Count -eq 1 -and $owners[0] -eq 'pedro') "owners=$($owners -join ',')"
    $dash = Send $pedro GET "$Bff/api/dashboard" $null $csrf
    Check '/api/dashboard via BFF: el 403 de la API llega tal cual' ($dash.Status -eq 403) "status=$($dash.Status)"
    Check 'sin renovaciones todavia (el token aun no esta por vencer)' ((Get-RefreshCount) -eq 0) "renovaciones=$(Get-RefreshCount)"

    Write-Output '--- 4. Logout ---'
    $wrongSid = Send $pedro GET "$Bff/bff/logout?sid=otro" $null
    Check 'logout con sid ajeno: 400 (anti logout-CSRF)' ($wrongSid.Status -eq 400) "status=$($wrongSid.Status)"
    $logout = Send $pedro GET "$Bff$($user.Json.logoutUrl)"
    Check 'logout: borra la cookie y va al end_session de Keycloak' ($logout.Status -eq 302 -and $logout.Location -like "$Keycloak/realms/helpdesk/protocol/openid-connect/logout*" -and $logout.Location -match 'id_token_hint=' -and (($logout.SetCookies -join ' ') -match '__Host-helpdesk=;')) ($logout.Location.Substring(0, [Math]::Min(110, $logout.Location.Length)) + '...')
    $kcLogout = Send $pedro GET $logout.Location
    Check 'Keycloak cierra su sesion y vuelve a /signout-callback-oidc' ($kcLogout.Status -eq 302 -and $kcLogout.Location -like "$Bff/signout-callback-oidc*") "status=$($kcLogout.Status) location=$($kcLogout.Location)"
    $back = Send $pedro GET $kcLogout.Location
    Check 'y el BFF termina en /' ($back.Status -eq 302 -and $back.Location -eq "$Bff/") "location=$($back.Location)"
    $after = Send $pedro GET "$Bff/bff/user"
    Check 'despues del logout: 401' ($after.Status -eq 401) "status=$($after.Status)"
    $again = Send $pedro GET "$Bff/bff/login?returnUrl=/"
    $kcPage = Get-KeycloakPage $pedro $again.Location
    Check 'y Keycloak vuelve a pedir la contrasena (su sesion tambien se cerro)' ($kcPage.Status -eq 200 -and $kcPage.Text -match 'kc-form-login') "status=$($kcPage.Status)"

    Write-Output '--- 5. Supervisor y open redirect ---'
    $laura = New-Jar
    $lauraLogin = Login-Bff $laura 'laura' 'https://evil.example/robar'
    Check 'returnUrl externo: termina en / (no hay open redirect)' ($lauraLogin.Callback.Location -eq "$Bff/") "location=$($lauraLogin.Callback.Location)"
    $lauraDash = Send $laura GET "$Bff/api/dashboard" $null $csrf
    Check 'laura via BFF: dashboard 200' ($lauraDash.Status -eq 200) "status=$($lauraDash.Status)"
}
finally {
    Stop-Process -Id $bffProcess.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Renueva cuando el token tiene mas de 5 segundos. Con un margen menor, "justo despues" depende de lo rapido que responda Keycloak:
# recien arrancado tarda mas de un segundo en cerrar la sesion, y el token ya estaria por renovarse otra vez.
Write-Output '--- 6. Renovacion del access token (BFF reiniciado: renueva si faltan menos de 4:55) ---'
$bffProcess = Start-Bff '00:04:55'
try {
    Start-Sleep -Seconds 6
    $renewed = Send $laura GET "$Bff/api/me" $null $csrf
    Check 'la cookie sobrevive al reinicio (llaves de Data Protection en disco)' ($renewed.Status -eq 200) "status=$($renewed.Status)"
    Check 'el BFF renovo el access token con el refresh token' ((Get-RefreshCount) -ge 1) "renovaciones=$(Get-RefreshCount)"
    Check 'y reemitio la cookie con los tokens nuevos' (@($renewed.SetCookies | Where-Object { $_ -like '__Host-helpdesk*' -and $_ -notmatch '__Host-helpdesk=;' }).Count -ge 1) ''

    # Un administrador cierra las sesiones de laura en Keycloak (usuario bloqueado, robo de sesion...).
    $adminToken = (Send (New-Jar) POST "$Keycloak/realms/master/protocol/openid-connect/token" @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = $adminPassword }).Json.access_token
    $auth = @{ Authorization = "Bearer $adminToken" }
    $lauraId = (Send (New-Jar) GET "$Keycloak/admin/realms/helpdesk/users?username=laura&exact=true" $null $auth).Json[0].id
    $kill = Send (New-Jar) POST "$Keycloak/admin/realms/helpdesk/users/$lauraId/logout" @{} $auth
    Check 'admin de Keycloak cierra las sesiones de laura' ($kill.Status -eq 204) "status=$($kill.Status)"
    $stillValid = Send $laura GET "$Bff/api/me" $null $csrf
    Check 'justo despues: 200 (el access token recien renovado sigue vigente)' ($stillValid.Status -eq 200) "status=$($stillValid.Status) renovaciones=$(Get-RefreshCount)"
    Start-Sleep -Seconds 6
    $revoked = Send $laura GET "$Bff/api/me" $null $csrf
    Check 'en la proxima renovacion Keycloak se niega: el BFF borra la cookie y responde 401' ($revoked.Status -eq 401 -and (($revoked.SetCookies -join ' ') -match '__Host-helpdesk=;')) "status=$($revoked.Status)"
}
finally {
    Stop-Process -Id $bffProcess.Id -Force -ErrorAction SilentlyContinue
}

Write-Output ("RESULTADO: {0} PASS, {1} FAIL" -f $script:pass, $script:fail)
