/* =====================================================================================
   Fase 4 · Bloque 3 · Query Store: encontrar lo que más cuesta y corregir planes SIN tocar la app.

   Query Store es la "caja negra" de cada base: guarda el texto de las consultas, cada plan que usaron
   y sus métricas por intervalo de tiempo. Vive dentro de la base, así que sobrevive a reinicios y a
   CLEAR PROCEDURE_CACHE (el caché de planes, en cambio, está en memoria y se pierde).

     1. Top 10 de consultas por lecturas lógicas totales (últimas 24 h).
     2. Consultas con más de un plan, donde un plan lee 10 veces más que otro (sniffing o regresión).
     3. Caso 6: forzar el plan con Index Seek en la página de tickets por agente.
     4. Caso 7: Query Store hint con el estimador de cardinalidad clásico en Q1 (Open + High).
     5. Estado final: planes forzados y hints activos, y si fallaron.

   query_id vs. query_hash:
   - query_id es UN texto exacto. El mismo SQL con saltos de línea CRLF (la app) y LF (un script) son
     dos query_id, y el número cambia de una máquina a otra: nunca lo escribas fijo en un script.
   - query_hash agrupa consultas de lógica PARECIDA: además de CRLF/LF, junta TOP (@p) con
     OFFSET/FETCH de la misma consulta. Sirve para sumar consumo (sección 1), no para elegir a qué
     consulta aplicarle un arreglo: para eso se busca por texto (secciones 3 y 4).
   Los arreglos son idempotentes y no tocan la consulta etiquetada «demo 05» de
   05-parameter-sniffing.sql, para que esa demostración siga mostrando el problema.

   OJO: este encabezado no usa comentarios anidados a propósito. Si un comentario de bloque contiene
   otro y después aparece un "?", sqlcmd (ODBC) no ejecuta el lote y NO muestra ningún error.

   Requisito: Query Store activo (lo enciende 05-parameter-sniffing.sql) y haber usado la API:
   GET /api/tickets?assignedTo=...  y  GET /api/tickets?status=Open&priority=High

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\06-query-store.sql
   ===================================================================================== */
SET NOCOUNT ON;
EXEC sys.sp_query_store_flush_db;

-- Incluye todo lo que corrió en la base (también el generador de datos y los CREATE INDEX):
-- así se ve en un servidor real, donde los procesos nocturnos suelen encabezar la lista.
PRINT N'### 1. Top 10 por lecturas lógicas totales (últimas 24 h, agrupadas por query_hash)';
WITH Executions AS (
    SELECT
        q.query_hash, q.query_id, rs.plan_id, rs.count_executions, rs.avg_logical_io_reads, rs.avg_cpu_time, rs.max_dop,
        -- Texto en una línea: la lista de parámetros y desde el FROM, que es lo que distingue una consulta de otra.
        LEFT(IIF(f.FromAt > 0, LEFT(t.OneLine, 45) + N' … ' + SUBSTRING(t.OneLine, f.FromAt + 1, 150), t.OneLine), 150) AS Texto
    FROM sys.query_store_runtime_stats AS rs
    JOIN sys.query_store_runtime_stats_interval AS i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
    JOIN sys.query_store_plan AS p ON p.plan_id = rs.plan_id
    JOIN sys.query_store_query AS q ON q.query_id = p.query_id
    JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
    CROSS APPLY (SELECT REPLACE(REPLACE(qt.query_sql_text, NCHAR(13), N''), NCHAR(10), N' ') AS OneLine) AS t
    CROSS APPLY (SELECT CHARINDEX(N' FROM ', t.OneLine) AS FromAt) AS f
    WHERE i.start_time >= DATEADD(HOUR, -24, SYSDATETIMEOFFSET())
      AND qt.query_sql_text NOT LIKE N'%sys.query_store%'   -- sin las consultas de monitoreo como esta
)
SELECT TOP (10)
    CONVERT(varchar(18), query_hash, 1)                                          AS QueryHash,
    COUNT(DISTINCT query_id)                                                     AS QueryIds,
    COUNT(DISTINCT plan_id)                                                      AS Planes,
    SUM(count_executions)                                                        AS Ejecuciones,
    CAST(SUM(avg_logical_io_reads * count_executions) AS bigint)                 AS LecturasTotales,
    CAST(SUM(avg_logical_io_reads * count_executions) / SUM(count_executions) AS bigint) AS LecturasPromedio,
    CAST(SUM(avg_cpu_time * count_executions) / 1000 AS bigint)                  AS CpuTotal_ms,
    MAX(max_dop)                                                                 AS DopMaximo,
    MIN(Texto)                                                                   AS Texto
FROM Executions
GROUP BY query_hash
ORDER BY LecturasTotales DESC;

PRINT N'### 2. Misma consulta, planes con rendimiento muy distinto (uno lee 10 veces más que otro)';
WITH PlanStats AS (
    SELECT
        p.query_id, p.plan_id, p.is_forced_plan, p.query_plan,
        SUM(rs.count_executions)                                                       AS Executions,
        SUM(rs.avg_logical_io_reads * rs.count_executions) / SUM(rs.count_executions)  AS AvgReads,
        MAX(rs.max_logical_io_reads)                                                   AS MaxReads
    FROM sys.query_store_plan AS p
    JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = p.plan_id
    JOIN sys.query_store_query AS q ON q.query_id = p.query_id
    JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
    WHERE qt.query_sql_text NOT LIKE N'%sys.query_store%'
    GROUP BY p.query_id, p.plan_id, p.is_forced_plan, p.query_plan
),
Spread AS (
    SELECT query_id, MAX(AvgReads) / IIF(MIN(AvgReads) < 1, 1, MIN(AvgReads)) AS Ratio
    FROM PlanStats
    GROUP BY query_id
    HAVING COUNT(*) > 1
)
SELECT
    ps.query_id,
    ps.plan_id,
    IIF(ps.is_forced_plan = 1, N'sí', N'')   AS Forzado,
    ps.Executions                            AS Ejecuciones,
    CAST(ps.AvgReads AS bigint)              AS LecturasPromedio,
    CAST(ps.MaxReads AS bigint)              AS LecturasMaximo,
    access.Operators                         AS AccesoADatos
FROM PlanStats AS ps
JOIN Spread AS s ON s.query_id = ps.query_id
-- El plan es XML: se puede consultar. Un Key Lookup es un Clustered Index Seek con Lookup="1".
CROSS APPLY (
    SELECT STRING_AGG(o.Operator, N' + ') AS Operators
    FROM (
        SELECT DISTINCT IIF(r.op.value('(*:IndexScan/@Lookup)[1]', 'bit') = 1, N'Key Lookup',
                            r.op.value('@PhysicalOp', 'nvarchar(60)') + N' ' + r.op.value('(*:IndexScan/*:Object/@Index)[1]', 'nvarchar(128)')) AS Operator
        FROM (SELECT TRY_CAST(ps.query_plan AS xml) AS PlanXml) AS x
        CROSS APPLY x.PlanXml.nodes('//*:RelOp[*:IndexScan]') AS r(op)
    ) AS o
) AS access
WHERE s.Ratio >= 10
ORDER BY s.Ratio DESC, ps.query_id, ps.plan_id;

PRINT N'### 3. Caso 6 · página de tickets por agente: forzar el plan con Index Seek';
-- Se busca por texto: el % entre fragmentos tolera CRLF y LF. Solo se puede forzar un plan que
-- Query Store ya vio para ESE query_id.
DECLARE @QueryId bigint, @PlanId bigint, @Changes int = 0, @Message nvarchar(400);
DECLARE AgentPageQueries CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        q.query_id,
        (SELECT MIN(p.plan_id)
         FROM sys.query_store_plan AS p
         WHERE p.query_id = q.query_id
           AND CAST(p.query_plan AS nvarchar(max)) LIKE N'%Index="\[IX_Tickets_AssignedTo\]"%' ESCAPE N'\'
           AND CAST(p.query_plan AS nvarchar(max)) NOT LIKE N'%PhysicalOp="Clustered Index Scan"%')
    FROM sys.query_store_query AS q
    JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
    WHERE q.query_parameterization_type = 1   -- 1 = User: llegó por sp_executesql con parámetros
      AND qt.query_sql_text LIKE N'%SELECT TOP(@p) \[t\].\[Id\], \[t\].\[AssignedTo\], %FROM \[Tickets\] AS \[t\]%WHERE \[t\].\[AssignedTo\] = @agent%ORDER BY \[t\].\[Id\]%' ESCAPE N'\'
      AND qt.query_sql_text NOT LIKE N'%demo 05%'
      AND NOT EXISTS (SELECT 1 FROM sys.query_store_plan AS f WHERE f.query_id = q.query_id AND f.is_forced_plan = 1);
OPEN AgentPageQueries;
FETCH AgentPageQueries INTO @QueryId, @PlanId;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @PlanId IS NULL
        SET @Message = CONCAT(N'query_id ', @QueryId, N': todavía no hay un plan con Index Seek que forzar. Limpia el caché ',
                              N'(ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE), llama a ',
                              N'GET /api/tickets?assignedTo=nadie y vuelve a correr el script.');
    ELSE
    BEGIN
        EXEC sys.sp_query_store_force_plan @query_id = @QueryId, @plan_id = @PlanId;
        SET @Message = CONCAT(N'query_id ', @QueryId, N': forzado el plan ', @PlanId, N' (Index Seek + Key Lookup).');
    END;
    PRINT @Message;
    SET @Changes += 1;
    FETCH AgentPageQueries INTO @QueryId, @PlanId;
END;
CLOSE AgentPageQueries;
DEALLOCATE AgentPageQueries;
IF @Changes = 0
    PRINT N'Nada nuevo: el plan ya estaba forzado, o la app aún no ejecutó la consulta (GET /api/tickets?assignedTo=pedro).';

PRINT N'### 4. Caso 7 · Q1 (Open + High): hint con el estimador de cardinalidad clásico';
-- El hint se agrega a la consulta en cada compilación, como si la app lo hubiera escrito en el SQL.
DECLARE @LegacyEstimator nvarchar(200) = N'OPTION (USE HINT(''FORCE_LEGACY_CARDINALITY_ESTIMATION''))';
SET @Changes = 0;
DECLARE OpenHighQueries CURSOR LOCAL FAST_FORWARD FOR
    SELECT q.query_id
    FROM sys.query_store_query AS q
    JOIN sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
    WHERE q.query_parameterization_type = 1
      AND qt.query_sql_text LIKE N'%SELECT TOP(@p) \[t\].\[Id\], \[t\].\[AssignedTo\], %FROM \[Tickets\] AS \[t\]%WHERE \[t\].\[Status\] = @status AND \[t\].\[Priority\] = @priority%ORDER BY \[t\].\[Id\]%' ESCAPE N'\'
      AND NOT EXISTS (SELECT 1 FROM sys.query_store_query_hints AS h
                      WHERE h.query_id = q.query_id AND h.query_hint_text = @LegacyEstimator);
OPEN OpenHighQueries;
FETCH OpenHighQueries INTO @QueryId;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC sys.sp_query_store_set_hints @query_id = @QueryId, @query_hints = @LegacyEstimator;
    PRINT CONCAT(N'query_id ', @QueryId, N': hint aplicado.');
    SET @Changes += 1;
    FETCH OpenHighQueries INTO @QueryId;
END;
CLOSE OpenHighQueries;
DEALLOCATE OpenHighQueries;
IF @Changes = 0
    PRINT N'Nada nuevo: el hint ya estaba aplicado, o la app aún no ejecutó la consulta (GET /api/tickets?status=Open&priority=High).';

PRINT N'### 5. Estado: planes forzados y hints activos';
-- Si el plan forzado deja de ser válido (por ejemplo, se borra el índice), SQL Server lo ignora y lo anota aquí.
SELECT N'plan forzado' AS Tipo, p.query_id, CAST(p.plan_id AS nvarchar(200)) AS Detalle,
       p.force_failure_count AS Fallos, p.last_force_failure_reason_desc AS UltimoFallo
FROM sys.query_store_plan AS p
WHERE p.is_forced_plan = 1
UNION ALL
SELECT N'hint', h.query_id, h.query_hint_text, h.query_hint_failure_count, h.last_query_hint_failure_reason_desc
FROM sys.query_store_query_hints AS h
ORDER BY query_id;

/* ### Deshacer: devuelve las consultas al optimizador (con los query_id y plan_id de la sección 5)
     EXEC sys.sp_query_store_unforce_plan @query_id = <query_id>, @plan_id = <plan_id>;
     EXEC sys.sp_query_store_clear_hints  @query_id = <query_id>;
*/
