<#
.SYNOPSIS
  Resume los planes reales (SET STATISTICS XML ON) de una salida de sqlcmd como árboles de operadores,
  con filas estimadas vs. reales: la misma información que el plan gráfico de SSMS, en texto.

.EXAMPLE
  .\sql\fase4\Resumir-Planes.ps1 -Path planes.txt
#>
param([Parameter(Mandatory)][string]$Path)

$text = [IO.File]::ReadAllText((Resolve-Path $Path), [Text.Encoding]::UTF8)
$plans = [regex]::Matches($text, '(?s)<ShowPlanXML.*?</ShowPlanXML>')
$titles = @([regex]::Matches($text, '(?m)^### .*$') | ForEach-Object { $_.Value.Trim() })

function Get-ActualRows([System.Xml.XmlElement]$relOp, [string]$attribute) {
    $counters = $relOp.SelectNodes('./p:RunTimeInformation/p:RunTimeCountersPerThread', $nsManager)
    ($counters | ForEach-Object { [double]$_.GetAttribute($attribute) } | Measure-Object -Sum).Sum
}

function Write-Operator([System.Xml.XmlElement]$relOp, [int]$depth) {
    $actualRows = Get-ActualRows $relOp 'ActualRows'
    $executions = Get-ActualRows $relOp 'ActualExecutions'
    # EstimateRows es "por ejecución". Las ejecuciones estimadas son 1 + rebinds + rewinds (lado interno de un
    # Nested Loops). OJO: en un plan paralelo, ActualExecutions cuenta HILOS, así que no sirve para multiplicar.
    $estimatedExecutions = 1 + [double]$relOp.EstimateRebinds + [double]$relOp.EstimateRewinds
    $estimated = [double]$relOp.EstimateRows * $estimatedExecutions

    # Tabla e índice que toca el operador (solo los de acceso a datos: Scan, Seek, Lookup).
    $object = $relOp.SelectSingleNode('./*/p:Object', $nsManager)
    $target = if ($object) { ' [' + (($object.Table, $object.Index | Where-Object { $_ }) -join '.' -replace '[\[\]]', '') + ']' } else { '' }

    # Con 0 ejecuciones (típico de Compute Scalar) el operador se evaluó dentro de otro: no hay cifra real que comparar.
    $ratio = [math]::Max($estimated, 1) / [math]::Max($actualRows, 1)
    $warning = if ($executions -gt 0 -and ($ratio -gt 10 -or $ratio -lt 0.1)) { '   <-- estimación muy lejos de la realidad' } else { '' }

    '{0}{1}{2}  | estimadas {3:N0} | reales {4:N0} | ejecuciones {5:N0}{6}' -f `
        ('    ' * $depth), $relOp.PhysicalOp, $target, $estimated, $actualRows, $executions, $warning

    foreach ($child in $relOp.SelectNodes('./*/p:RelOp', $nsManager)) {
        Write-Operator $child ($depth + 1)
    }
}

for ($i = 0; $i -lt $plans.Count; $i++) {
    [xml]$xml = $plans[$i].Value
    $script:nsManager = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $nsManager.AddNamespace('p', 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')

    $statement = $xml.SelectSingleNode('//p:StmtSimple', $nsManager)
    $grant = $xml.SelectSingleNode('//p:MemoryGrantInfo', $nsManager)
    "`n" + $(if ($i -lt $titles.Count) { $titles[$i] } else { "### Consulta $($i + 1)" })
    'Costo estimado: {0:N1} | Memoria concedida: {1:N0} KB | Paralelismo (DOP): {2}' -f `
        [double]$statement.StatementSubTreeCost, [double]($grant.GrantedMemory), $xml.SelectSingleNode('//p:QueryPlan', $nsManager).DegreeOfParallelism

    foreach ($group in $xml.SelectNodes('//p:MissingIndexGroup', $nsManager)) {
        $columns = ($group.MissingIndex.ColumnGroup | ForEach-Object {
            "$($_.Usage): " + (($_.Column | ForEach-Object { $_.Name -replace '[\[\]]', '' }) -join ', ')
        }) -join ' | '
        'Índice que SQL Server echa de menos (impacto estimado {0:N0} %): {1}' -f [double]$group.Impact, $columns
    }

    Write-Operator $xml.SelectSingleNode('//p:QueryPlan/p:RelOp', $nsManager) 0
}
