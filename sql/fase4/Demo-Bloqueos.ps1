<#
.SYNOPSIS
  Reproduce, con el SQL que genera EF Core, lo que pasa durante POST /api/agents/{name}/reassign.
  Todo termina en ROLLBACK: los datos no cambian.

  1. Bloqueos. Una sesión reasigna los tickets abiertos de pedro a luis y deja la transacción abierta
     unos segundos, como la app mientras inserta un comentario por ticket. Mientras tanto, otras sesiones
     hacen cosas que no tienen que ver con pedro. Se mide cuánto esperó cada una y, a la mitad, se toma
     una foto con 08-quien-bloquea.sql.
  2. Deadlock. Dos reasignaciones en sentido contrario al mismo tiempo (pedro → luis y luis → pedro),
     y el grafo leído con 09-ultimo-deadlock.sql.

.EXAMPLE
  .\sql\fase4\Demo-Bloqueos.ps1
.EXAMPLE
  .\sql\fase4\Demo-Bloqueos.ps1 -Parte Deadlock
#>
param(
    [ValidateSet('Todo', 'Bloqueos', 'Deadlock')][string]$Parte = 'Todo',
    [int]$SegundosAbierta = 6,
    [string]$Servidor = '127.0.0.1,1433'
)

# La clave de sa está en deploy/.env (fuera de git), la misma que usa docker compose.
$envFile = Join-Path $PSScriptRoot '..\..\deploy\.env'
$password = ([IO.File]::ReadAllLines($envFile) | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -replace '^MSSQL_SA_PASSWORD=', ''
$connection = @('-S', $Servidor, '-U', 'sa', '-P', $password, '-d', 'HelpDesk', '-C', '-I', '-f', '65001')
$workDir = Join-Path ([IO.Path]::GetTempPath()) "demo-bloqueos-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workDir | Out-Null

function New-SqlFile([string]$name, [string]$sql) {
    $path = Join-Path $workDir "$name.sql"
    [IO.File]::WriteAllText($path, $sql, (New-Object Text.UTF8Encoding($false)))
    $path
}

# Cada sesión es un sqlcmd aparte. -H pone el nombre del "equipo": así se reconoce en sys.dm_exec_sessions.
function Start-Session([string]$name, [string]$sql) {
    $output = Join-Path $workDir "$name.txt"
    $arguments = ($connection + @('-H', $name, '-i', (New-SqlFile $name $sql), '-o', $output)) |
        ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }
    [pscustomobject]@{ Name = $name; Output = $output; Process = (Start-Process sqlcmd -ArgumentList $arguments -PassThru -WindowStyle Hidden) }
}

function Invoke-SqlFile([string]$path) {
    & sqlcmd @connection -W -s ' | ' -i $path
}

# El UPDATE de ExecuteUpdate tal como lo guardó Query Store, dentro de una transacción que no se confirma.
function Get-ReassignSql([string]$from, [string]$to, [int]$holdSeconds) {
    @"
SET NOCOUNT ON;
BEGIN TRANSACTION;
EXEC sp_executesql N'UPDATE [t]
SET [t].[AssignedTo] = @p,
    [t].[UpdatedAt] = @p2
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @fromAgent AND [t].[Status] IN (N''Open'', N''InProgress'')',
    N'@fromAgent nvarchar(50),@p nvarchar(50),@p2 datetimeoffset(7)',
    @fromAgent = N'$from', @p = N'$to', @p2 = '2026-10-26T00:00:00+00:00';
PRINT CONCAT(N'filas=', @@ROWCOUNT);
WAITFOR DELAY '00:00:$('{0:D2}' -f $holdSeconds)';
ROLLBACK;
"@
}

function Wait-UntilHolding([string]$hostName) {
    $probe = New-SqlFile 'probe' @"
SET NOCOUNT ON;
SELECT COUNT(*) FROM sys.dm_exec_sessions AS s JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
WHERE s.host_name = N'$hostName' AND r.wait_type = N'WAITFOR';
"@
    foreach ($attempt in 1..150) {
        if ((& sqlcmd @connection -h -1 -W -i $probe | Where-Object { $_ -match '^\d+$' }) -eq '1') { return }
        Start-Sleep -Milliseconds 200
    }
    throw "La sesión $hostName no llegó a mantener la transacción abierta."
}

try {
    if ($Parte -in 'Todo', 'Bloqueos') {
        "### 1. Bloqueos: pedro → luis queda sin confirmar $SegundosAbierta s"
        $writer = Start-Session 'demo-reasignacion' (Get-ReassignSql 'pedro' 'luis' $SegundosAbierta)
        Wait-UntilHolding 'demo-reasignacion'

        # Cada operación mide su propia espera. Las lecturas previas (los Id) no esperan: con RCSI leen la versión confirmada.
        $measure = "SET NOCOUNT ON;`nDECLARE @Start datetime2 = SYSDATETIME(), @Rows int;`n{0}`nPRINT CONCAT(N'espera_ms=', DATEDIFF(MILLISECOND, @Start, SYSDATETIME()), N';filas=', @Rows);"
        $luisTicket = "DECLARE @Id int = (SELECT MIN(Id) FROM dbo.Tickets WHERE AssignedTo = N'luis' AND Status = N'Open');"
        $pedroTicket = "DECLARE @Id int = (SELECT MIN(Id) FROM dbo.Tickets WHERE AssignedTo = N'pedro' AND Status = N'Open');"
        $victims = [ordered]@{
            'Leer los tickets abiertos de pedro (RCSI, como la app)' =
                "SELECT @Rows = COUNT(*) FROM dbo.Tickets WHERE AssignedTo = N'pedro' AND Status IN (N'Open', N'InProgress');"
            'Lo mismo sin RCSI (READCOMMITTEDLOCK)' =
                "SELECT @Rows = COUNT(*) FROM dbo.Tickets WITH (READCOMMITTEDLOCK) WHERE AssignedTo = N'pedro' AND Status IN (N'Open', N'InProgress');"
            'Editar un ticket de luis' =
                "$luisTicket BEGIN TRANSACTION; UPDATE dbo.Tickets SET Priority = N'Critical' WHERE Id = @Id; SET @Rows = @@ROWCOUNT; ROLLBACK;"
            'Comentar un ticket de luis' =
                "$luisTicket BEGIN TRANSACTION; INSERT INTO dbo.TicketComments (TicketId, Author, Body, CreatedAt) VALUES (@Id, N'luis', N'Revisando.', SYSDATETIMEOFFSET()); SET @Rows = @@ROWCOUNT; ROLLBACK;"
            'Crear un ticket nuevo' =
                "BEGIN TRANSACTION; INSERT INTO dbo.Tickets (Id, Title, Priority, Status, CreatedAt, AssignedTo) VALUES (NEXT VALUE FOR dbo.TicketIds, N'Demo de bloqueos', N'Low', N'Open', SYSDATETIMEOFFSET(), N'ana'); SET @Rows = @@ROWCOUNT; ROLLBACK;"
            'Editar un ticket de pedro (de los que se reasignan)' =
                "$pedroTicket BEGIN TRANSACTION; UPDATE dbo.Tickets SET Priority = N'Critical' WHERE Id = @Id; SET @Rows = @@ROWCOUNT; ROLLBACK;"
        }
        $number = 0
        $sessions = foreach ($victim in $victims.GetEnumerator()) {
            $number++
            $session = Start-Session "demo-operacion-$number" ($measure -f $victim.Value)
            $session | Add-Member -NotePropertyName Operation -NotePropertyValue $victim.Key -PassThru
        }

        Start-Sleep -Seconds 2
        "--- Foto a los 2 s (08-quien-bloquea.sql):"
        Invoke-SqlFile (Join-Path $PSScriptRoot '08-quien-bloquea.sql')

        ($sessions + $writer).Process | Wait-Process
        "--- Resultado:"
        $sessions | ForEach-Object {
            $text = Get-Content $_.Output -Raw -Encoding UTF8
            $wait = if ($text -match 'espera_ms=(\d+);filas=(\d*)') { '{0:N0} ms' -f [int]$Matches[1] } else { 'error' }
            $rows = if ($text -match 'filas=(\d+)') { $Matches[1] } else { ($text -split "`n" | Where-Object { $_ -match 'Msg|error' } | Select-Object -First 1) }
            [pscustomobject]@{ Operacion = $_.Operation; Espero = $wait; Filas = $rows }
        } | Format-Table -AutoSize -Wrap
    }

    if ($Parte -in 'Todo', 'Deadlock') {
        '### 2. Deadlock: pedro → luis y luis → pedro al mismo tiempo'
        $pair = @(
            (Start-Session 'demo-pedro-a-luis' (Get-ReassignSql 'pedro' 'luis' 2)),
            (Start-Session 'demo-luis-a-pedro' (Get-ReassignSql 'luis' 'pedro' 2)))
        $pair.Process | Wait-Process
        $victim = $pair | Where-Object { (Get-Content $_.Output -Raw -Encoding UTF8) -match 'Msg 1205' }
        if ($victim) {
            "Víctima: $($victim.Name) (error 1205). La otra terminó su UPDATE normalmente."
            Invoke-SqlFile (Join-Path $PSScriptRoot '09-ultimo-deadlock.sql') | Where-Object { $_ -notmatch '^<deadlock' }
        }
        else {
            'Esta vez no hubo deadlock: depende del momento exacto en que cada UPDATE llega a la página del otro.'
        }
    }
}
finally {
    Remove-Item $workDir -Recurse -Force
}
