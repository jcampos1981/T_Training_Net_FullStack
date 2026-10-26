/* =====================================================================================
   Fase 4 · Línea base: las consultas que la API manda HOY a SQL Server, medidas antes de tunear.

   Son copia del SQL que genera EF Core (o Dapper) y llegan igual que desde la app:
   parametrizadas con sp_executesql, que es lo que determina cómo se cachea y reutiliza el plan.

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -f 65001 -i sql\fase4\02-linea-base.sql -o salida.txt
   En SSMS: activa "Include Actual Execution Plan" (Ctrl+M) para ver el plan gráfico de cada una.
   ===================================================================================== */
SET NOCOUNT ON;
SET STATISTICS IO ON;   -- lecturas lógicas por tabla: la métrica principal (no depende de la caché ni del disco)
SET STATISTICS TIME ON; -- CPU y tiempo transcurrido

PRINT N'### Q1 · GET /api/tickets?status=Open&priority=High';
EXEC sp_executesql N'SELECT [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[Status] = @status AND [t].[Priority] = @priority
ORDER BY [t].[Id]',
    N'@status nvarchar(20), @priority nvarchar(20)', @status = N'Open', @priority = N'High';

PRINT N'### Q2a · GET /api/tickets?assignedTo=ana  (1 % de los tickets)';
EXEC sp_executesql N'SELECT [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]',
    N'@agent nvarchar(50)', @agent = N'ana';

PRINT N'### Q2b · GET /api/tickets?assignedTo=pedro  (30 % de los tickets)';
EXEC sp_executesql N'SELECT [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]',
    N'@agent nvarchar(50)', @agent = N'pedro';

PRINT N'### Q3 · GET /api/tickets/activity?latestComments=3';
EXEC sp_executesql N'-- Actividad de tickets abiertos

SELECT [t].[Id], [t].[Title], [t].[Status], (
    SELECT COUNT(*)
    FROM [TicketComments] AS [t0]
    WHERE [t0].[TicketId] = [t].[Id]), [t3].[Id], [t3].[Author], [t3].[Body], [t3].[CreatedAt]
FROM [Tickets] AS [t]
LEFT JOIN (
    SELECT [t2].[Id], [t2].[Author], [t2].[Body], [t2].[CreatedAt], [t2].[TicketId]
    FROM (
        SELECT [t1].[Id], [t1].[Author], [t1].[Body], [t1].[CreatedAt], [t1].[TicketId], ROW_NUMBER() OVER(PARTITION BY [t1].[TicketId] ORDER BY [t1].[CreatedAt] DESC) AS [row]
        FROM [TicketComments] AS [t1]
    ) AS [t2]
    WHERE [t2].[row] <= @latestComments
) AS [t3] ON [t].[Id] = [t3].[TicketId]
WHERE [t].[Status] IN (N''Open'', N''InProgress'')
ORDER BY [t].[Id], [t3].[TicketId], [t3].[CreatedAt] DESC',
    N'@latestComments int', @latestComments = 3;

PRINT N'### Q4 · GET /api/dashboard  (carga abierta por agente, SQL de Dapper)';
EXEC sp_executesql N'SELECT
    COALESCE(t.AssignedTo, N''(sin asignar)'')                  AS Agent,
    COUNT(*)                                                    AS OpenTickets,
    SUM(CASE WHEN t.Priority = N''Critical'' THEN 1 ELSE 0 END) AS CriticalTickets,
    DATEDIFF(HOUR, MIN(t.CreatedAt), @Now)                      AS OldestOpenHours
FROM dbo.Tickets AS t
WHERE t.Status IN (N''Open'', N''InProgress'')
GROUP BY t.AssignedTo
ORDER BY CriticalTickets DESC, OpenTickets DESC;',
    N'@Now datetimeoffset', @Now = '2026-10-26T00:00:00+00:00';

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
