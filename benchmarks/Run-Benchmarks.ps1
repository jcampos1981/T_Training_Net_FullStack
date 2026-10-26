<#
.SYNOPSIS
  Corre los benchmarks de HelpDesk contra la base de desarrollo. BenchmarkDotNet exige Release: en Debug
  el compilador no optimiza y los números no significan nada.

.EXAMPLE
  .\benchmarks\Run-Benchmarks.ps1                       # todos
.EXAMPLE
  .\benchmarks\Run-Benchmarks.ps1 --filter '*ById*'     # los argumentos se pasan tal cual a BenchmarkDotNet
#>

# La clave de sa está en deploy/.env (fuera de git), la misma que usa docker compose.
$envFile = Join-Path $PSScriptRoot '..\deploy\.env'
$password = ([IO.File]::ReadAllLines($envFile) | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -replace '^MSSQL_SA_PASSWORD=', ''
$env:ConnectionStrings__HelpDesk = "Server=127.0.0.1,1433;Database=HelpDesk;User Id=sa;Password=$password;TrustServerCertificate=True"

$filter = if ($args.Count -gt 0) { $args } else { @('--filter', '*') }
dotnet run -c Release --project (Join-Path $PSScriptRoot 'HelpDesk.Benchmarks') -- @filter --artifacts (Join-Path $PSScriptRoot 'BenchmarkDotNet.Artifacts')
