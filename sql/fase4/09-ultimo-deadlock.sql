/* =====================================================================================
   Fase 4 · Bloque 4 · El último deadlock, leído en tablas.

   SQL Server guarda cada deadlock en la sesión de eventos system_health, que viene encendida de
   fábrica: no hay que preparar nada antes. Primero se busca en su ring buffer (memoria, inmediato) y,
   si ya no está ahí, en sus archivos .xel (pueden tardar unos segundos en recibirlo).

     1. El grafo en XML. En SSMS: clic en el XML, "Guardar como" .xdl y se abre como dibujo.
     2. Los procesos: quién fue la víctima, qué pedía, con qué aislamiento y qué lote ejecutaba.
     3. Los recursos: qué tenía cada proceso y qué esperaba. Un deadlock es un ciclo:
        A tiene lo que B espera, y B tiene lo que A espera.

   Los bloqueos de página no traen el nombre del índice, pero sí associatedObjectId: el hobt_id del
   índice, que sys.partitions traduce. (Traducir el número de página no sirve: si la página se
   reutilizó desde el deadlock, hoy puede pertenecer a otro índice o estar libre.)

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\09-ultimo-deadlock.sql
   (-I es obligatorio: los métodos de XML necesitan QUOTED_IDENTIFIER ON.)
   ===================================================================================== */
SET NOCOUNT ON;

DECLARE @Deadlock xml = (
    SELECT TOP (1) e.x.query('data/value/deadlock')
    FROM (
        SELECT CAST(t.target_data AS xml) AS Ring
        FROM sys.dm_xe_session_targets AS t
        JOIN sys.dm_xe_sessions AS s ON s.address = t.event_session_address
        WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer'
    ) AS rb
    CROSS APPLY rb.Ring.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS e(x)
    ORDER BY e.x.value('@timestamp', 'datetime2') DESC);

IF @Deadlock IS NULL
    SET @Deadlock = (
        SELECT TOP (1) CAST(event_data AS xml).query('event/data/value/deadlock')
        FROM sys.fn_xe_file_target_read_file(N'system_health*.xel', NULL, NULL, NULL)
        WHERE object_name = N'xml_deadlock_report'
        ORDER BY timestamp_utc DESC);

IF @Deadlock IS NULL
BEGIN
    PRINT N'No hay deadlocks registrados en system_health.';
    RETURN;
END;

PRINT N'### 1. Grafo (XML)';
SELECT @Deadlock AS Grafo;

PRINT N'### 2. Procesos';
SELECT
    IIF(p.x.value('@id', 'nvarchar(50)') = @Deadlock.value('(deadlock/victim-list/victimProcess/@id)[1]', 'nvarchar(50)'),
        N'VÍCTIMA', N'')                                          AS Rol,
    p.x.value('@spid', 'int')                                     AS Sesion,
    p.x.value('@isolationlevel', 'nvarchar(40)')                  AS Aislamiento,
    p.x.value('@lockMode', 'nvarchar(10)')                        AS Pedia,
    p.x.value('@waitresource', 'nvarchar(100)')                   AS EsperabaPor,
    p.x.value('@logused', 'bigint')                               AS LogUsado_bytes,
    LEFT(REPLACE(REPLACE(LTRIM(p.x.value('(inputbuf)[1]', 'nvarchar(max)')), NCHAR(13), N''), NCHAR(10), N' '), 150) AS Lote
FROM @Deadlock.nodes('deadlock/process-list/process') AS p(x);

PRINT N'### 3. Recursos: quién tenía qué y quién lo esperaba';
WITH Processes AS (
    SELECT p.x.value('@id', 'nvarchar(50)') AS ProcessId, p.x.value('@spid', 'int') AS Spid
    FROM @Deadlock.nodes('deadlock/process-list/process') AS p(x)
),
Resources AS (
    SELECT
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL))    AS Number,
        r.x.value('local-name(.)', 'nvarchar(40)')    AS Kind,
        r.x.value('@objectname', 'nvarchar(200)')     AS ObjectName,
        r.x.value('@indexname', 'nvarchar(200)')      AS IndexName,
        r.x.value('@dbid', 'int')                     AS DatabaseId,
        r.x.value('@associatedObjectId', 'bigint')    AS HobtId,
        r.x.query('.')                                AS Node
    FROM @Deadlock.nodes('deadlock/resource-list/*') AS r(x)
)
SELECT
    res.Number                                            AS Recurso,
    res.Kind                                              AS Tipo,
    res.ObjectName                                        AS Objeto,
    COALESCE(NULLIF(res.IndexName, N''), hobt.IndexName, N'') AS Indice,
    holder.Role                                           AS Rol,
    pr.Spid                                               AS Sesion,
    holder.Mode                                           AS Modo
FROM Resources AS res
CROSS APPLY (
    SELECT N'tiene' AS Role, o.x.value('@id', 'nvarchar(50)') AS ProcessId, o.x.value('@mode', 'nvarchar(10)') AS Mode
    FROM res.Node.nodes('*/owner-list/owner') AS o(x)
    UNION ALL
    SELECT N'espera', w.x.value('@id', 'nvarchar(50)'), w.x.value('@mode', 'nvarchar(10)')
    FROM res.Node.nodes('*/waiter-list/waiter') AS w(x)
) AS holder
JOIN Processes AS pr ON pr.ProcessId = holder.ProcessId
OUTER APPLY (
    SELECT i.name AS IndexName
    FROM sys.partitions AS p
    JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
    WHERE p.hobt_id = res.HobtId AND res.DatabaseId = DB_ID()
) AS hobt
ORDER BY res.Number, holder.Role DESC;
