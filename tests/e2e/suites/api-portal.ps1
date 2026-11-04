param(
    [string]$Api = 'http://localhost:5159',
    [string]$Realm = 'http://localhost:8080/realms/helpdesk',
    [string]$Repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
)

Add-Type -AssemblyName System.Net.Http
$ErrorActionPreference = 'Stop'
$secrets = Get-Content "$env:APPDATA\Microsoft\UserSecrets\d0995617-ac24-4e5d-97fc-fee84e147509\secrets.json" -Raw | ConvertFrom-Json
$password = $secrets.'DevelopmentUsers:Password'
$adminPassword = ((Get-Content "$Repo\deploy\.env") | Where-Object { $_ -like 'KEYCLOAK_ADMIN_PASSWORD=*' }) -replace "^KEYCLOAK_ADMIN_PASSWORD='?([^']*)'?$", '$1'

$script:pass = 0; $script:fail = 0
function Check([string]$name, $ok, [string]$detail = '') {
    if ([bool]$ok) { $script:pass++; Write-Output ("PASS  {0}  {1}" -f $name, $detail) }
    else { $script:fail++; Write-Output ("FAIL  {0}  {1}" -f $name, $detail) }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$http = New-Object System.Net.Http.HttpClient($handler)

function Send([string]$method, [string]$url, [string]$token, $json, [hashtable]$form, [string]$cookie) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($method), $url)
    if ($token) { $request.Headers.TryAddWithoutValidation('Authorization', "Bearer $token") | Out-Null }
    if ($cookie) { $request.Headers.TryAddWithoutValidation('Cookie', $cookie) | Out-Null }
    if ($null -ne $json) { $request.Content = [System.Net.Http.StringContent]::new(($json | ConvertTo-Json -Depth 5), [Text.Encoding]::UTF8, 'application/json') }
    if ($form) {
        $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
        foreach ($k in $form.Keys) { $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' -ArgumentList $k, ([string]$form[$k]))) }
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    }
    $response = $http.SendAsync($request).Result
    $text = $response.Content.ReadAsStringAsync().Result
    $body = $null; if ($text) { try { $body = $text | ConvertFrom-Json } catch { } }
    $cookies = (($response.Headers | Where-Object { $_.Key -eq 'Set-Cookie' }).Value | Where-Object { $_ } | ForEach-Object { $_.Split(';')[0] }) -join '; '
    $location = if ($response.Headers.Location) { $response.Headers.Location.ToString() } else { $null }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $body; Text = $text; Location = $location; Cookies = $cookies }
}
function Get-Jwt([string]$email) { (Send POST "$Api/api/token" $null @{ email = $email; password = $password }).Json.accessToken }

$camila = Get-Jwt 'camila@acme.local'
$diego = Get-Jwt 'diego@acme.local'
$pedro = Get-Jwt 'pedro@helpdesk.local'
$laura = Get-Jwt 'laura@helpdesk.local'
$created = @()

try {
    Write-Output '--- 1. Cada cliente ve solo lo suyo ---'
    $me = Send GET "$Api/api/me" $camila
    Check '/api/me camila: Customer, sin agente' ((@($me.Json.roles) -join ',') -eq 'Customer' -and -not $me.Json.agent) $me.Text
    $mine = Send GET "$Api/api/portal/tickets" $camila
    Check 'camila: sus 3 tickets del seeder' ($mine.Status -eq 200 -and @($mine.Json.items).Count -eq 3) ((@($mine.Json.items) | ForEach-Object { "$($_.code) $($_.title)" }) -join ' | ')
    Check 'el contrato del portal no expone assignedTo ni version' (-not ($mine.Text -match 'assignedTo|version')) ''
    $diegoMine = Send GET "$Api/api/portal/tickets" $diego
    Check 'diego: sus 2 tickets' (@($diegoMine.Json.items).Count -eq 2) ''
    $camilaTicket = @($mine.Json.items)[0].id
    $foreign = Send GET "$Api/api/portal/tickets/$camilaTicket" $diego
    Check 'diego pide un ticket de camila: 404' ($foreign.Status -eq 404) "status=$($foreign.Status)"
    $foreignComments = Send GET "$Api/api/portal/tickets/$camilaTicket/comments" $diego
    Check 'diego pide los comentarios de camila: 404' ($foreignComments.Status -eq 404) "status=$($foreignComments.Status)"
    $staffTicket = Send GET "$Api/api/portal/tickets/1" $camila
    Check 'camila pide un ticket interno (Id 1): 404' ($staffTicket.Status -eq 404) "status=$($staffTicket.Status)"
    $missing = Send GET "$Api/api/portal/tickets/999999999" $camila
    Check 'y uno que no existe: tambien 404 (no se distinguen)' ($missing.Status -eq 404) "status=$($missing.Status)"

    Write-Output '--- 2. Abrir y conversar ---'
    $open = Send POST "$Api/api/portal/tickets" $camila @{ title = 'El correo no sincroniza en el celular'; description = 'Desde ayer el celular no muestra correos nuevos; en el computador si llegan.'; priority = 'Medium' }
    $newId = $open.Json.id; if ($newId) { $created += $newId }
    Check 'abrir ticket: 201 con Location al portal' ($open.Status -eq 201 -and $open.Location -like "*/api/portal/tickets/$newId") "status=$($open.Status) location=$($open.Location) code=$($open.Json.code)"
    $conversation = Send GET "$Api/api/portal/tickets/$newId/comments" $camila
    Check 'la descripcion es el primer comentario, firmado por camila' (@($conversation.Json).Count -eq 1 -and @($conversation.Json)[0].author -eq 'camila') $conversation.Text
    $critical = Send POST "$Api/api/portal/tickets" $camila @{ title = 'Todo se cayo, urgente'; description = 'Necesito ayuda inmediata con el sistema.'; priority = 'Critical' }
    Check 'prioridad Critical desde el portal: 400' ($critical.Status -eq 400 -and $critical.Text -match 'Low, Medium o High') "status=$($critical.Status)"
    $noDescription = Send POST "$Api/api/portal/tickets" $camila @{ title = 'Sin descripcion aqui' }
    Check 'sin descripcion: 400' ($noDescription.Status -eq 400) "status=$($noDescription.Status)"
    $reply = Send POST "$Api/api/portal/tickets/$newId/comments" $camila @{ body = 'Adjunto: el celular es un Android 15.' }
    Check 'camila responde en su ticket: 201' ($reply.Status -eq 201 -and $reply.Json.author -eq 'camila') "status=$($reply.Status)"
    $foreignReply = Send POST "$Api/api/portal/tickets/$newId/comments" $diego @{ body = 'Intento de diego' }
    Check 'diego intenta responder en el ticket de camila: 404' ($foreignReply.Status -eq 404) "status=$($foreignReply.Status)"

    Write-Output '--- 3. Vista del equipo y permisos cruzados ---'
    $staffView = Send GET "$Api/api/tickets/$newId" $laura
    Check 'laura (Supervisor) ve requestedBy en la API del equipo' ($staffView.Status -eq 200 -and $staffView.Json.requestedBy -eq 'camila@acme.local' -and -not $staffView.Json.assignedTo) "requestedBy=$($staffView.Json.requestedBy)"
    $byCustomer = Send GET "$Api/api/tickets?requestedBy=camila@acme.local&pageSize=10" $laura
    Check 'filtro ?requestedBy= en /api/tickets: los 4 de camila' (@($byCustomer.Json.items).Count -eq 4) "count=$(@($byCustomer.Json.items).Count)"
    $pedroView = Send GET "$Api/api/tickets/$newId" $pedro
    Check 'pedro (Agent) no lo ve: no esta asignado a el (403)' ($pedroView.Status -eq 403) "status=$($pedroView.Status)"
    $pedroPortal = Send GET "$Api/api/portal/tickets" $pedro
    Check 'pedro en el portal: 403 (no es Customer)' ($pedroPortal.Status -eq 403) "status=$($pedroPortal.Status)"
    $customerStaff = Send GET "$Api/api/tickets" $camila
    Check 'camila en /api/tickets: 403' ($customerStaff.Status -eq 403) "status=$($customerStaff.Status)"
    $customerDash = Send GET "$Api/api/dashboard" $camila
    Check 'camila en /api/dashboard: 403' ($customerDash.Status -eq 403) "status=$($customerDash.Status)"

    $close = Send PUT "$Api/api/tickets/$newId" $laura @{ title = $staffView.Json.title; priority = 'Medium'; status = 'Closed'; assignedTo = $null; version = $staffView.Json.version }
    Check 'laura cierra el ticket' ($close.Status -eq 200 -and $close.Json.status -eq 'Closed') "status=$($close.Status) $($close.Json.detail)"
    $closedReply = Send POST "$Api/api/portal/tickets/$newId/comments" $camila @{ body = 'Sigue sin funcionar.' }
    Check 'camila responde en un ticket cerrado: 409' ($closedReply.Status -eq 409) "status=$($closedReply.Status)"
    $closedView = Send GET "$Api/api/portal/tickets/$newId" $camila
    Check 'y en el portal lo ve Closed' ($closedView.Json.status -eq 'Closed') ''

    Write-Output '--- 4. SQL Server: el indice filtrado sirve con un parametro ---'
    $sql = "SET NOCOUNT ON; SET STATISTICS PROFILE ON;`nGO`nEXEC sp_executesql N'SELECT TOP(51) [t].[Id], [t].[Title] FROM [Tickets] AS [t] WHERE [t].[RequestedBy] = @p ORDER BY [t].[Id]', N'@p nvarchar(256)', @p = N'camila@acme.local';`nGO"
    $sqlFile = Join-Path $env:TEMP 'portal-plan.sql'
    [IO.File]::WriteAllText($sqlFile, $sql, (New-Object Text.UTF8Encoding($false)))
    $saPassword = ((Get-Content "$Repo\deploy\.env") | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -replace "^MSSQL_SA_PASSWORD='?([^']*)'?$", '$1'
    $plan = sqlcmd -S '127.0.0.1,1433' -U sa -P $saPassword -d HelpDesk -C -I -f 65001 -i $sqlFile -W -h -1 2>&1 | Out-String
    Remove-Item $sqlFile
    $seek = ([regex]::Match($plan, 'Index Seek\(OBJECT:\(\[HelpDesk\]\.\[dbo\]\.\[Tickets\]\.\[IX_Tickets_RequestedBy\][^|]*')).Value
    Check 'el plan usa Index Seek sobre IX_Tickets_RequestedBy con @p' ($seek -match 'SEEK:\(\[t\]\.\[RequestedBy\]=\[@p\]\)') $seek

    Write-Output '--- 5. Keycloak: el mismo cliente, roles y autorregistro ---'
    # Authorization Code + PKCE con el client de Scalar (copiado de keycloak-test.ps1, en forma compacta).
    function Get-KeycloakToken([hashtable]$login, [switch]$Register) {
        $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $verifier = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $challenge = [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $redirect = "$Api/scalar/"
        $page = Send GET ("$Realm/protocol/openid-connect/auth?client_id=helpdesk-scalar&response_type=code&scope=openid%20email&state=s&code_challenge=$challenge&code_challenge_method=S256&redirect_uri=" + [Uri]::EscapeDataString($redirect))
        $cookie = $page.Cookies
        if ($Register) {
            $link = [Net.WebUtility]::HtmlDecode([regex]::Match($page.Text, 'href="([^"]*registration[^"]*)"').Groups[1].Value)
            if ($link -notlike 'http*') { $link = "http://localhost:8080$link" }
            $page = Send GET $link $null $null $null $cookie
            if ($page.Cookies) { $cookie = "$cookie; $($page.Cookies)" }
            $formTag = [regex]::Match($page.Text, '<form[^>]*kc-register-form[^>]*>').Value
        } else {
            $formTag = [regex]::Match($page.Text, '<form[^>]*kc-form-login[^>]*>').Value
        }
        $action = [Net.WebUtility]::HtmlDecode([regex]::Match($formTag, 'action="([^"]+)"').Groups[1].Value)
        $posted = Send POST $action $null $null $login $cookie
        if ($posted.Status -ne 302 -or $posted.Location -notmatch '[?&]code=') { return [pscustomobject]@{ Error = "status=$($posted.Status) location=$($posted.Location)"; Html = $posted.Text } }
        $code = [Uri]::UnescapeDataString([regex]::Match($posted.Location, '[?&]code=([^&]+)').Groups[1].Value)
        (Send POST "$Realm/protocol/openid-connect/token" $null $null @{ grant_type = 'authorization_code'; code = $code; redirect_uri = $redirect; client_id = 'helpdesk-scalar'; code_verifier = $verifier }).Json
    }
    function Get-RealmRoles([string]$jwt) {
        $p = $jwt.Split('.')[1].Replace('-', '+').Replace('_', '/'); switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
        @(([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json).realm_access.roles)
    }

    $kcPedro = Get-KeycloakToken @{ username = 'pedro'; password = $password; credentialId = '' }
    Check 'Keycloak: pedro NO recibe Customer (los usuarios importados no llevan los roles por defecto)' ((Get-RealmRoles $kcPedro.access_token) -notcontains 'Customer') ((Get-RealmRoles $kcPedro.access_token) -join ',')
    $kcCamila = Get-KeycloakToken @{ username = 'camila'; password = $password; credentialId = '' }
    $kcMine = Send GET "$Api/api/portal/tickets" $kcCamila.access_token
    Check 'camila por Keycloak ve los mismos tickets (la llave es el email)' ($kcMine.Status -eq 200 -and @($kcMine.Json.items).Count -eq 4) "count=$(@($kcMine.Json.items).Count)"

    $newUser = 'prueba.registro'
    $registered = Get-KeycloakToken @{ firstName = 'Prueba'; lastName = 'Registro'; email = "$newUser@globex.local"; username = $newUser; password = $password; 'password-confirm' = $password } -Register
    Check 'autorregistro en Keycloak: entrega tokens' ($registered.access_token) $registered.Error
    if ($registered.access_token) {
        Check 'el registrado recibe Customer por los roles por defecto' ((Get-RealmRoles $registered.access_token) -contains 'Customer') ((Get-RealmRoles $registered.access_token) -join ',')
        $regPortal = Send GET "$Api/api/portal/tickets" $registered.access_token
        Check 'su portal: 200, sin tickets' ($regPortal.Status -eq 200 -and @($regPortal.Json.items).Count -eq 0) "status=$($regPortal.Status)"
        $regStaff = Send GET "$Api/api/tickets" $registered.access_token
        Check 'y la API del equipo: 403' ($regStaff.Status -eq 403) "status=$($regStaff.Status)"
    }
}
finally {
    Write-Output '--- Limpieza ---'
    foreach ($id in $created) { $d = Send DELETE "$Api/api/tickets/$id" $laura; Write-Output "      DELETE ticket $id -> $($d.Status)" }
    $adminToken = (Send POST 'http://localhost:8080/realms/master/protocol/openid-connect/token' $null $null @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'admin'; password = $adminPassword }).Json.access_token
    $found = (Send GET "http://localhost:8080/admin/realms/helpdesk/users?username=prueba.registro&exact=true" $adminToken).Json
    foreach ($u in @($found)) { if ($u.id) { $d = Send DELETE "http://localhost:8080/admin/realms/helpdesk/users/$($u.id)" $adminToken; Write-Output "      DELETE usuario Keycloak $($u.username) -> $($d.Status)" } }
}

Write-Output ("RESULTADO: {0} PASS, {1} FAIL" -f $script:pass, $script:fail)
