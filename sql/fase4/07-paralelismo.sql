/* =====================================================================================
   Fase 4 · Bloque 3 · Paralelismo: desde qué costo y con cuántos hilos.

   - cost threshold for parallelism (CTFP): costo estimado a partir del cual el optimizador considera
     un plan paralelo. El valor de fábrica (5) viene de los años 90, cuando un costo de 5 era una
     consulta pesada; hoy lo supera cualquier consulta que lea unos pocos miles de páginas.
   - max degree of parallelism (MAXDOP): cuántos hilos puede usar cada operador paralelo. 0 = todos.
     OPTION (MAXDOP n) limita los hilos, pero NO salta el umbral: si el costo no llega al CTFP, el plan
     es serial aunque la consulta pida 12 hilos.

     1. Configuración actual y CPUs del servidor.
     2. Valores de fábrica (CTFP 5, MAXDOP 0) y Q5 (GET /api/agents) en UNA sesión con MAXDOP 1, 2, 4,
        8 y 12: CPU contra tiempo de respuesta.
     3. Configuración recomendada: CTFP 50 y MAXDOP 8.
     4. Q5 sin hint con la configuración nueva: qué plan elige ahora.

   La misma consulta con 12 sesiones a la vez se mide con Medir-Concurrencia.ps1.

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\07-paralelismo.sql
   Cambia la configuración del SERVIDOR y vacía el caché de planes: solo para desarrollo.
   (Q5 cuenta con COUNT(CASE ...), por eso SQL Server avisa "Null value is eliminated": es normal.)
   ===================================================================================== */
SET NOCOUNT ON;

PRINT N'### 1. Configuración actual';
SELECT name AS Opcion, value_in_use AS Valor
FROM sys.configurations
WHERE name IN (N'cost threshold for parallelism', N'max degree of parallelism');
SELECT
    cpu_count AS CpusLogicos,
    (SELECT COUNT(*) FROM sys.dm_os_nodes WHERE node_state_desc = N'ONLINE') AS NodosNuma
FROM sys.dm_os_sys_info;

-- SQL exacto de EF Core para GET /api/agents (sin el comentario de TagWith).
DECLARE @Q5 nvarchar(max) = N'SELECT [t].[AssignedTo], COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END), COUNT(*)
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] IS NOT NULL
GROUP BY [t].[AssignedTo]
ORDER BY COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END) DESC, [t].[AssignedTo]';
-- INSERT ... EXEC descarta las filas (no interesan aquí) sin cambiar el plan de la consulta.
DECLARE @Discard TABLE (Agent nvarchar(50), OpenTickets int, Total int);
-- Las métricas viven en el caché de planes, y cada RECONFIGURE de estas opciones vacía el caché
-- de TODA la instancia: se copian aquí antes de cambiar la configuración.
DECLARE @Results TABLE (Variante nvarchar(20), Ejecuciones bigint, DopReal bigint, Cpu_ms decimal(8, 1),
                        Transcurrido_ms decimal(8, 1), Lecturas bigint, CostoEstimado decimal(10, 2));
DECLARE @Run int, @Dop int, @Sql nvarchar(max);

PRINT N'### 2. Valores de fábrica y Q5 en una sola sesión, 6 ejecuciones por variante';
EXEC sys.sp_configure N'show advanced options', 1;
RECONFIGURE;
EXEC sys.sp_configure N'cost threshold for parallelism', 5;
EXEC sys.sp_configure N'max degree of parallelism', 0;
RECONFIGURE;
ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;

DECLARE Variants CURSOR LOCAL FAST_FORWARD FOR
    SELECT Dop FROM (VALUES (1), (2), (4), (8), (12)) AS v(Dop);
OPEN Variants;
FETCH Variants INTO @Dop;
WHILE @@FETCH_STATUS = 0
BEGIN
    -- La etiqueta al inicio deja cada variante como una entrada aparte en sys.dm_exec_query_stats.
    SET @Sql = CONCAT(N'/* medir-dop=', @Dop, N' */ ', @Q5, N' OPTION (MAXDOP ', @Dop, N')');
    SET @Run = 0;
    WHILE @Run < 6
    BEGIN
        INSERT INTO @Discard EXEC sp_executesql @Sql;
        SET @Run += 1;
    END;
    FETCH Variants INTO @Dop;
END;
CLOSE Variants;
DEALLOCATE Variants;

INSERT INTO @Results
SELECT
    SUBSTRING(st.text, 4, CHARINDEX(N' */', st.text) - 4),
    qs.execution_count,
    qs.last_dop,
    qs.total_worker_time / qs.execution_count / 1000.0,
    qs.total_elapsed_time / qs.execution_count / 1000.0,
    qs.total_logical_reads / qs.execution_count,
    qp.query_plan.value('declare namespace p="http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                         (//p:StmtSimple/@StatementSubTreeCost)[1]', 'decimal(10, 2)')
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
WHERE st.text LIKE N'/* medir-dop=%';

PRINT N'### 3. Configuración recomendada: CTFP 50 y MAXDOP 8';
-- MAXDOP 8: recomendación de Microsoft para un solo nodo NUMA con más de 8 CPUs lógicos.
-- CTFP 50: punto de partida habitual; después se ajusta mirando el costo de los planes reales.
EXEC sys.sp_configure N'cost threshold for parallelism', 50;
EXEC sys.sp_configure N'max degree of parallelism', 8;
RECONFIGURE;

PRINT N'### 4. Q5 sin hint con la configuración nueva';
SET @Sql = N'/* medir-config */ ' + @Q5;
SET @Run = 0;
WHILE @Run < 6
BEGIN
    INSERT INTO @Discard EXEC sp_executesql @Sql;
    SET @Run += 1;
END;

INSERT INTO @Results
SELECT
    N'CTFP 50, MAXDOP 8',
    qs.execution_count,
    qs.last_dop,
    qs.total_worker_time / qs.execution_count / 1000.0,
    qs.total_elapsed_time / qs.execution_count / 1000.0,
    qs.total_logical_reads / qs.execution_count,
    qp.query_plan.value('declare namespace p="http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                         (//p:StmtSimple/@StatementSubTreeCost)[1]', 'decimal(10, 2)')
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
WHERE st.text LIKE N'/* medir-config */%';

PRINT N'### Resultados (promedios por ejecución)';
SELECT * FROM @Results ORDER BY DopReal, Variante;
