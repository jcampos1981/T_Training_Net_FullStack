-- Carga para Medir-Concurrencia.ps1: Q5 (GET /api/agents) cinco veces seguidas con MAXDOP $(DOP).
-- $(DOP) es una variable de sqlcmd (sqlcmd -v DOP=12). La etiqueta del inicio separa cada variante
-- en sys.dm_exec_query_stats, e INSERT ... EXEC descarta las filas sin cambiar el plan.
SET NOCOUNT ON;
DECLARE @Discard TABLE (Agent nvarchar(50), OpenTickets int, Total int);
DECLARE @Run int = 0;
WHILE @Run < 5
BEGIN
    INSERT INTO @Discard EXEC sp_executesql N'/* carga-dop=$(DOP) */ SELECT [t].[AssignedTo], COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END), COUNT(*)
FROM [Tickets] AS [t]
WHERE [t].[AssignedTo] IS NOT NULL
GROUP BY [t].[AssignedTo]
ORDER BY COUNT(CASE
    WHEN [t].[Status] IN (N''Open'', N''InProgress'') THEN 1
END) DESC, [t].[AssignedTo]
OPTION (MAXDOP $(DOP))';
    SET @Run += 1;
END;
