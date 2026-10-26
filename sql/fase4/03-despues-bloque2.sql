/* =====================================================================================
   Fase 4 · Bloque 2 · Las mismas consultas DESPUÉS de: paginación keyset, índice filtrado,
   actividad reescrita con OUTER APPLY y resumen de agentes con GROUP BY en SQL.
   SQL copiado del log de EF Core (y de Dapper), con los parámetros que manda la API.

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -i sql\fase4\03-despues-bloque2.sql -o salida.txt
   ===================================================================================== */
SET NOCOUNT ON;
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

PRINT N'### Q1 · página 1 de Open + High (TOP 51)';
EXEC sp_executesql N'SELECT TOP(@p) [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[Status] = @status AND [t].[Priority] = @priority
ORDER BY [t].[Id]',
    N'@p int, @status nvarchar(20), @priority nvarchar(20)', @p = 51, @status = N'Open', @priority = N'High';

PRINT N'### Q2a · página 1 de ana (TOP 51)';
EXEC sp_executesql N'SELECT TOP(@p) [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]',
    N'@p int, @agent nvarchar(50)', @p = 51, @agent = N'ana';

PRINT N'### Q2b · página 1 de pedro (TOP 51)';
EXEC sp_executesql N'SELECT TOP(@p) [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]',
    N'@p int, @agent nvarchar(50)', @p = 51, @agent = N'pedro';

PRINT N'### Q2c · página profunda de pedro con KEYSET (after = 950,000)';
EXEC sp_executesql N'SELECT TOP(@p) [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent AND [t].[Id] > @afterId
ORDER BY [t].[Id]',
    N'@p int, @agent nvarchar(50), @afterId int', @p = 51, @agent = N'pedro', @afterId = 950000;

PRINT N'### Q2d · la misma página profunda con OFFSET (saltando 285,000 filas)';
EXEC sp_executesql N'SELECT [t].[Id], [t].[AssignedTo], [t].[CreatedAt], [t].[Priority], [t].[Status], [t].[Title], [t].[UpdatedAt], [t].[Version]
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] = @agent
ORDER BY [t].[Id]
OFFSET @skip ROWS FETCH NEXT @p ROWS ONLY',
    N'@p int, @agent nvarchar(50), @skip int', @p = 51, @agent = N'pedro', @skip = 285000;

PRINT N'### Q3 · actividad reescrita (OUTER APPLY, página de 51, últimos 3)';
EXEC sp_executesql N'-- Actividad de tickets abiertos (página por keyset)
SELECT t.Id, t.Title, t.Status, cc.CommentCount,
       c.Id AS CommentId, c.Author, c.Body, c.CreatedAt
FROM (
    SELECT TOP (@Take) tk.Id, tk.Title, tk.Status
    FROM dbo.Tickets AS tk
    WHERE tk.Status IN (N''Open'', N''InProgress'') AND tk.Id > @After
    ORDER BY tk.Id
) AS t
CROSS APPLY (
    SELECT COUNT(*) AS CommentCount
    FROM dbo.TicketComments AS x
    WHERE x.TicketId = t.Id
) AS cc
OUTER APPLY (
    SELECT TOP (@LatestComments) y.Id, y.Author, y.Body, y.CreatedAt
    FROM dbo.TicketComments AS y
    WHERE y.TicketId = t.Id
    ORDER BY y.CreatedAt DESC
) AS c
ORDER BY t.Id, c.CreatedAt DESC;',
    N'@Take int, @After int, @LatestComments int', @Take = 51, @After = 0, @LatestComments = 3;

PRINT N'### Q4 · dashboard: carga abierta por agente (mismo SQL, ahora con índice filtrado)';
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

PRINT N'### Q5 · GET /api/agents (GROUP BY en SQL; antes: 1 M de tickets agrupados en memoria)';
EXEC sp_executesql N'-- Resumen de carga por agente

SELECT [t].[AssignedTo], COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END), COUNT(*)
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] IS NOT NULL
GROUP BY [t].[AssignedTo]
ORDER BY COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END) DESC, [t].[AssignedTo]';

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
