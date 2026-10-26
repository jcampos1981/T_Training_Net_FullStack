/* =====================================================================================
   Fase 4 · Bloque 3 · Estadísticas: lo que el optimizador "sabe" de los datos.

   El optimizador no cuenta filas: ESTIMA cuántas devolverá cada operador usando estadísticas
   (un histograma de hasta 200 "escalones" por columna + densidades de combinaciones de columnas).
   Con esa estimación elige el plan. Estimación mala → plan malo.

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\04-estadisticas.sql
   ===================================================================================== */
SET NOCOUNT ON;

PRINT N'### 1. Estadísticas de la tabla Tickets (las de índices y las creadas solas por SQL Server)';
SELECT
    s.name                                   AS Estadistica,
    STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY sc.stats_column_id) AS Columnas,
    IIF(s.auto_created = 1, N'automática', N'de índice') AS Origen,
    sp.rows                                  AS Filas,
    sp.rows_sampled                          AS Muestreadas,
    sp.modification_counter                  AS CambiosDesdeActualizacion,
    CONVERT(varchar(16), sp.last_updated, 120) AS Actualizada
FROM sys.stats AS s
JOIN sys.stats_columns AS sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
CROSS APPLY sys.dm_db_stats_properties(s.object_id, s.stats_id) AS sp
WHERE s.object_id = OBJECT_ID(N'dbo.Tickets')
GROUP BY s.name, s.auto_created, sp.rows, sp.rows_sampled, sp.modification_counter, sp.last_updated
ORDER BY s.name;

PRINT N'### 2. Histograma de IX_Tickets_Status_Priority (solo de la PRIMERA columna: Status)';
DBCC SHOW_STATISTICS (N'dbo.Tickets', N'IX_Tickets_Status_Priority') WITH HISTOGRAM, NO_INFOMSGS;

PRINT N'### 3. Densidades del mismo índice (1 / número de combinaciones distintas)';
DBCC SHOW_STATISTICS (N'dbo.Tickets', N'IX_Tickets_Status_Priority') WITH DENSITY_VECTOR, NO_INFOMSGS;

PRINT N'### 4. Realidad: tickets por Status y Priority';
SELECT Status, Priority, COUNT(*) AS Tickets
FROM dbo.Tickets
WHERE Status IN (N'Open', N'InProgress')
GROUP BY Status, Priority
ORDER BY Status, Priority;

PRINT N'### 5. Open + High (reales: 1,996): cuántas filas estima cada estimador y qué plan elige';
-- SQL de EF Core para GET /api/tickets?status=Open&priority=High: todas las filas (Fase 3) y la página
-- de 51 (Fase 4). El estimador "clásico" es el de SQL Server 7 a 2012; el nuevo llegó en 2014 y es el
-- de fábrica (nivel de compatibilidad 170). INSERT ... EXEC descarta las filas sin cambiar el plan.
ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
DECLARE @Ticket nvarchar(max) = N'[t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[Status] = @status AND [t].[Priority] = @priority';
DECLARE @Legacy nvarchar(100) = N' OPTION (USE HINT(''FORCE_LEGACY_CARDINALITY_ESTIMATION''))';
DECLARE @Params nvarchar(100) = N'@p int, @status nvarchar(20), @priority nvarchar(20)';
DECLARE @Discard TABLE (Id int, AssignedTo nvarchar(50), CreatedAt datetimeoffset, Priority nvarchar(20),
                        Status nvarchar(20), Title nvarchar(200), UpdatedAt datetimeoffset, Version varbinary(8));
-- La etiqueta va después del SELECT y separa cada variante en el caché de planes.
DECLARE @Variants TABLE (Tag nvarchar(30), Statement nvarchar(max));
INSERT INTO @Variants VALUES
    (N'todas, CE nuevo',    N'SELECT /* todas, CE nuevo */ ' + @Ticket),
    (N'todas, CE clásico',  N'SELECT /* todas, CE clásico */ ' + @Ticket + @Legacy),
    (N'página, CE nuevo',   N'SELECT /* página, CE nuevo */ TOP(@p) ' + @Ticket + N' ORDER BY [t].[Id]'),
    (N'página, CE clásico', N'SELECT /* página, CE clásico */ TOP(@p) ' + @Ticket + N' ORDER BY [t].[Id]' + @Legacy);

-- Las lecturas se leen de STATISTICS IO, línea "Table 'Tickets'": la de la tabla variable que recibe
-- las filas va aparte. (sys.dm_exec_query_stats las sumaría: unas 2 lecturas más por fila.)
DECLARE @Tag nvarchar(30), @Statement nvarchar(max);
DECLARE Variants CURSOR LOCAL FAST_FORWARD FOR SELECT Tag, Statement FROM @Variants;
SET STATISTICS IO ON;
OPEN Variants;
FETCH Variants INTO @Tag, @Statement;
WHILE @@FETCH_STATUS = 0
BEGIN
    PRINT @Tag;
    INSERT INTO @Discard EXEC sp_executesql @Statement, @Params, @p = 51, @status = N'Open', @priority = N'High';
    FETCH Variants INTO @Tag, @Statement;
END;
CLOSE Variants;
DEALLOCATE Variants;
SET STATISTICS IO OFF;

-- En el caché, el texto de sp_executesql empieza con la lista de parámetros: "(@p int, ...)SELECT ...".
WITH XMLNAMESPACES (DEFAULT N'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
SELECT
    v.Tag                                                                         AS Variante,
    CAST(qp.query_plan.value(N'(//StmtSimple/@StatementEstRows)[1]', 'float') AS int) AS FilasEstimadas,
    qs.last_rows                                                                  AS FilasReales,
    qp.query_plan.value(N'(//RelOp[IndexScan]/@PhysicalOp)[1]', 'nvarchar(60)')   AS Acceso
FROM @Variants AS v
JOIN sys.dm_exec_query_stats AS qs ON 1 = 1
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
WHERE st.text = N'(' + @Params + N')' + v.Statement   -- con LIKE, los [ ] del SQL serían comodines
ORDER BY v.Tag DESC;
