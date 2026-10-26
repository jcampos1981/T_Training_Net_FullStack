<#
.SYNOPSIS
  Ejecuta carga-q5.sql desde varias sesiones a la vez, como varios usuarios llamando a GET /api/agents,
  una vez por cada MAXDOP pedido. Muestra el tiempo total de cada prueba (incluye el arranque de cada
  sqlcmd) y, según SQL Server, la CPU y el tiempo promedio de cada consulta.

  Durante la prueba baja el cost threshold for parallelism al valor de fábrica (5) y al final restaura
  el que había: Q5 cuesta 5.24, así que con el umbral recomendado (50) nunca sería paralela.

.EXAMPLE
  .\sql\fase4\Medir-Concurrencia.ps1                     # 12 sesiones, MAXDOP 12 y 1
.EXAMPLE
  .\sql\fase4\Medir-Concurrencia.ps1 -Sesiones 4 -Dop 8, 1
#>
param(
    [int]$Sesiones = 12,
    [int[]]$Dop = @(12, 1),
    [string]$Servidor = '127.0.0.1,1433'
)

# La clave de sa está en deploy/.env (fuera de git), la misma que usa docker compose.
$envFile = Join-Path $PSScriptRoot '..\..\deploy\.env'
$password = ([IO.File]::ReadAllLines($envFile) | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -replace '^MSSQL_SA_PASSWORD=', ''
$connection = @('-S', $Servidor, '-U', 'sa', '-P', $password, '-d', 'HelpDesk', '-C', '-I')

function Invoke-Sql([string]$sql, [string[]]$options = @()) {
    # Pasar SQL con comillas en -Q falla en PowerShell 5.1: un archivo temporal evita el problema.
    $file = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($file.FullName, $sql, (New-Object Text.UTF8Encoding($false)))
        & sqlcmd @connection -f 65001 -W @options -i $file.FullName
    }
    finally { Remove-Item $file.FullName }
}

$originalThreshold = Invoke-Sql "SET NOCOUNT ON; SELECT value_in_use FROM sys.configurations WHERE name = N'cost threshold for parallelism';" @('-h', '-1') |
    Where-Object { $_ -match '^\d+$' }
Invoke-Sql @"
EXEC sys.sp_configure N'show advanced options', 1;
RECONFIGURE;
EXEC sys.sp_configure N'cost threshold for parallelism', 5;
RECONFIGURE;
ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
"@ | Out-Null

try {
    $load = Join-Path $PSScriptRoot 'carga-q5.sql'
    $totals = foreach ($degree in $Dop) {
        # Start-Process no pone comillas a los argumentos: se agregan a los que tienen espacios (una ruta, por
        # ejemplo) para que no se partan en dos. A las opciones no: sqlcmd no reconoce "-S" entre comillas.
        $arguments = ($connection + @('-v', "DOP=$degree", '-i', $load)) | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $sessions = 1..$Sesiones | ForEach-Object { Start-Process sqlcmd -ArgumentList $arguments -PassThru -WindowStyle Hidden }
        $sessions | Wait-Process
        [pscustomobject]@{ MAXDOP = $degree; Sesiones = $Sesiones; Consultas = $Sesiones * 5; TiempoTotal_s = [math]::Round($clock.Elapsed.TotalSeconds, 2) }
    }
    $totals | Format-Table -AutoSize

    # Si dos sesiones compilan la misma consulta al mismo tiempo, el caché puede tener dos entradas: se suman.
    'Promedio por consulta según SQL Server:'
    Invoke-Sql @"
SET NOCOUNT ON;
SELECT
    v.Variante,
    SUM(qs.execution_count)                                                           AS Ejecuciones,
    MAX(qs.last_dop)                                                                  AS DopReal,
    CAST(SUM(qs.total_worker_time) / SUM(qs.execution_count) / 1000.0 AS decimal(8, 1))  AS Cpu_ms,
    CAST(SUM(qs.total_elapsed_time) / SUM(qs.execution_count) / 1000.0 AS decimal(8, 1)) AS Transcurrido_ms,
    CAST(MAX(qs.max_elapsed_time) / 1000.0 AS decimal(8, 1))                          AS TranscurridoMax_ms
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY (SELECT SUBSTRING(st.text, 4, CHARINDEX(N' */', st.text) - 4) AS Variante) AS v
WHERE st.text LIKE N'/* carga-dop=%'
GROUP BY v.Variante
ORDER BY DopReal DESC;
"@ @('-s', ' | ')
}
finally {
    # Restaurar el umbral vacía el caché de planes: por eso las métricas se leen antes, dentro del try.
    Invoke-Sql "EXEC sys.sp_configure N'cost threshold for parallelism', $originalThreshold; RECONFIGURE;" | Out-Null
}
