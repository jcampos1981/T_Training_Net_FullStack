/* =====================================================================================
   Fase 4 · Bloque 3 · Parameter sniffing y row goal con la página de tickets por agente.

   La consulta es la que genera EF Core para GET /api/tickets?assignedTo=...  (TOP 51, keyset).
   SQL Server compila el plan con los valores de la PRIMERA ejecución ("olfatea" los parámetros)
   y lo reutiliza con cualquier valor posterior.

   La etiqueta «demo 05» es un comentario DENTRO de la consulta, a propósito: Query Store descarta los
   comentarios que van antes del SELECT (como el de TagWith en EF Core), pero conserva los de adentro.
   Así esta demostración tiene su propio query_id, distinto del de la app, y los arreglos de
   06-query-store.sql (que se aplican a la app) no la alteran. El comentario no cambia el plan.

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -i sql\fase4\05-parameter-sniffing.sql
   (Limpia el caché de planes de ESTA base: solo para desarrollo.)
   ===================================================================================== */
SET NOCOUNT ON;

-- Query Store guarda el historial de consultas, planes y métricas. En desarrollo capturamos TODO
-- (en producción conviene AUTO, que descarta las consultas triviales).
ALTER DATABASE CURRENT SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL);

-- Encenderlo es asíncrono: si medimos antes de que quede en READ_WRITE, la demostración no se registra.
DECLARE @Waited int = 0;
WHILE (SELECT actual_state_desc FROM sys.database_query_store_options) <> N'READ_WRITE' AND @Waited < 30
BEGIN
    WAITFOR DELAY '00:00:01';
    SET @Waited += 1;
END;

DECLARE @PageByAgent nvarchar(max) = N'SELECT /* demo 05 */ TOP(@p) [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]';
DECLARE @Params nvarchar(200) = N'@p int, @agent nvarchar(50)';

SET STATISTICS IO ON;

PRINT N'### A1 · caché vacío → primera ejecución con pedro (compila el plan para pedro)';
ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
EXEC sp_executesql @PageByAgent, @Params, @p = 51, @agent = N'pedro';
PRINT N'### A2 · mismo plan, ahora con un agente SIN tickets';
EXEC sp_executesql @PageByAgent, @Params, @p = 51, @agent = N'nadie';

PRINT N'### B1 · caché vacío → primera ejecución con el agente SIN tickets';
ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
EXEC sp_executesql @PageByAgent, @Params, @p = 51, @agent = N'nadie';
PRINT N'### B2 · mismo plan, ahora con pedro';
EXEC sp_executesql @PageByAgent, @Params, @p = 51, @agent = N'pedro';

SET STATISTICS IO OFF;

-- Query Store ya vio la misma consulta con DOS planes distintos.
-- query_parameterization_type = 1 (User) deja solo la consulta de sp_executesql, no las de este script.
EXEC sys.sp_query_store_flush_db;
PRINT N'### Query Store: planes de esta consulta (acumulado de todas las corridas del script)';
SELECT
    q.query_id,
    p.plan_id,
    CASE WHEN CAST(p.query_plan AS nvarchar(max)) LIKE N'%PhysicalOp="Clustered Index Scan"%'
         THEN N'Clustered Index Scan (row goal)' ELSE N'Index Seek + Key Lookup' END AS Plan_,
    SUM(rs.count_executions)                 AS Ejecuciones,
    CAST(AVG(rs.avg_logical_io_reads) AS int) AS LecturasPromedio,
    CAST(MAX(rs.max_logical_io_reads) AS int) AS LecturasMaximo
FROM sys.query_store_query_text AS qt
JOIN sys.query_store_query AS q ON q.query_text_id = qt.query_text_id
JOIN sys.query_store_plan AS p ON p.query_id = q.query_id
JOIN sys.query_store_runtime_stats AS rs ON rs.plan_id = p.plan_id
WHERE qt.query_sql_text LIKE N'%/* demo 05 */%'
  AND q.query_parameterization_type = 1
GROUP BY q.query_id, p.plan_id, CAST(p.query_plan AS nvarchar(max))
ORDER BY p.plan_id;
