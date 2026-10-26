/* =====================================================================================
   Fase 4 · Genera ~1 millón de tickets y ~2.5 millones de comentarios para practicar tuning.

   - Datos SESGADOS como en la vida real (80 % cerrados, un agente con 30 % de la carga, otro con 1 %).
   - DETERMINISTA: el "azar" sale de HASHBYTES('SHA2_256', Id), así que siempre se generan los mismos
     datos y las mediciones de antes y después se pueden comparar.
     (No usar CHECKSUM: está hecho para detectar cambios, no para repartir valores; con Ids consecutivos
     produce patrones y la primera versión de este script generó 1 ticket "Open" en vez de ~10,000.)
   - Por lotes de 100,000 filas: cada lote es una transacción corta y el log no crece sin control.

   Uso (desde la raíz del repo):
     sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -f 65001 -i sql\fase4\01-generar-datos.sql
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Base de desarrollo sin respaldos de log: con SIMPLE el log se reutiliza en vez de crecer.
ALTER DATABASE CURRENT SET RECOVERY SIMPLE;

DECLARE @TargetTickets int = 1000000;
DECLARE @BatchSize     int = 100000;
DECLARE @HistoryStart  datetimeoffset = '2023-10-01T00:00:00+00:00';
DECLARE @HistoryDays   int = 3 * 365;
DECLARE @Now           datetimeoffset = SYSUTCDATETIME();

DECLARE @LastId int = (SELECT ISNULL(MAX(Id), 0) FROM dbo.Tickets);
DECLARE @ToCreate int = @TargetTickets - (SELECT COUNT(*) FROM dbo.Tickets);
DECLARE @Created int = 0;
DECLARE @Message nvarchar(200);

WHILE @Created < @ToCreate
BEGIN
    DECLARE @FirstId int = @LastId + @Created + 1;
    DECLARE @ThisBatch int = IIF(@ToCreate - @Created < @BatchSize, @ToCreate - @Created, @BatchSize);

    -- Números 0..N-1 sin tabla auxiliar: producto cartesiano de una vista del sistema + TOP.
    WITH Numbers AS (
        SELECT TOP (@ThisBatch) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
        FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b
    ),
    Seeds AS (
        -- Un hash de 32 bytes por fila; cada "dado" usa 4 bytes distintos (& 0x7FFFFFFF evita negativos).
        SELECT
            x.Id,
            (CAST(SUBSTRING(x.Hash,  1, 4) AS int) & 2147483647) % 100 AS StatusRoll,
            (CAST(SUBSTRING(x.Hash,  5, 4) AS int) & 2147483647) % 100 AS PriorityRoll,
            (CAST(SUBSTRING(x.Hash,  9, 4) AS int) & 2147483647) % 100 AS AgentRoll,
            (CAST(SUBSTRING(x.Hash, 13, 4) AS int) & 2147483647) % 12  AS TitleRoll,
            (CAST(SUBSTRING(x.Hash, 17, 4) AS int) & 2147483647)       AS TimeRoll
        FROM Numbers
        CROSS APPLY (SELECT @FirstId + n AS Id, HASHBYTES('SHA2_256', CAST(@FirstId + n AS varbinary(4))) AS Hash) AS x
    )
    INSERT INTO dbo.Tickets (Id, Title, Priority, Status, CreatedAt, AssignedTo)
    SELECT
        s.Id,
        CHOOSE(s.TitleRoll + 1,
            N'No puedo iniciar sesión', N'Impresora sin tóner', N'Error 500 al guardar factura',
            N'VPN se desconecta', N'Correo no sincroniza', N'Solicitud de cuenta nueva',
            N'Pantalla azul al encender', N'Instalar Office', N'Reporte de ventas lento',
            N'Cambio de contraseña', N'Wifi de la sala de juntas', N'Disco del servidor lleno'),
        CASE WHEN s.PriorityRoll < 40 THEN N'Low'
             WHEN s.PriorityRoll < 75 THEN N'Medium'
             WHEN s.PriorityRoll < 95 THEN N'High'
             ELSE N'Critical' END,
        st.Status,
        -- Los abiertos son recientes (últimos 30 días); los cerrados se reparten en 3 años de historia.
        CASE WHEN st.Status IN (N'Open', N'InProgress')
             THEN DATEADD(SECOND, -(s.TimeRoll % (30 * 86400)), @Now)
             ELSE DATEADD(SECOND, s.TimeRoll % 86400, DATEADD(DAY, s.TimeRoll % @HistoryDays, @HistoryStart)) END,
        CASE WHEN s.AgentRoll < 30 THEN N'pedro'    -- 30 %: el agente más cargado
             WHEN s.AgentRoll < 50 THEN N'luis'
             WHEN s.AgentRoll < 65 THEN N'marta'
             WHEN s.AgentRoll < 77 THEN N'sofia'
             WHEN s.AgentRoll < 87 THEN N'jorge'
             WHEN s.AgentRoll < 94 THEN N'carlos'
             WHEN s.AgentRoll < 98 THEN N'elena'
             WHEN s.AgentRoll < 99 THEN N'ana'      --  1 %: casi no tiene tickets
             ELSE NULL END                          --  1 %: sin asignar
    FROM Seeds AS s
    CROSS APPLY (SELECT CASE WHEN s.StatusRoll < 1  THEN N'Open'
                             WHEN s.StatusRoll < 5  THEN N'InProgress'
                             WHEN s.StatusRoll < 20 THEN N'Resolved'
                             ELSE N'Closed' END AS Status) AS st;

    -- De 0 a 5 comentarios por ticket (promedio 2.5), escritos a partir de la fecha del ticket.
    INSERT INTO dbo.TicketComments (TicketId, Author, Body, CreatedAt)
    SELECT
        t.Id,
        CHOOSE(CAST(SUBSTRING(c.Hash, 1, 1) AS int) % 4 + 1, N'pedro', N'luis', N'marta', N'sistema'),
        CHOOSE(CAST(SUBSTRING(c.Hash, 2, 1) AS int) % 5 + 1,
            N'Le pedí al usuario una captura del error.', N'Lo reproduje en el ambiente de pruebas.',
            N'Escalado con el proveedor; esperamos respuesta.', N'Aplicada la solución, pendiente de confirmar.',
            N'El usuario confirma que ya funciona.'),
        DATEADD(HOUR, k.n * 3, t.CreatedAt)
    FROM dbo.Tickets AS t
    CROSS APPLY (SELECT (CAST(SUBSTRING(HASHBYTES('SHA2_256', CAST(t.Id AS varbinary(4))), 21, 4) AS int) & 2147483647) % 6 AS CommentCount) AS cc
    CROSS APPLY (SELECT TOP (cc.CommentCount) v.n FROM (VALUES (1), (2), (3), (4), (5)) AS v(n) ORDER BY v.n) AS k
    CROSS APPLY (SELECT HASHBYTES('MD5', CAST(t.Id AS varbinary(4)) + CAST(k.n AS varbinary(1))) AS Hash) AS c
    WHERE t.Id BETWEEN @FirstId AND @FirstId + @ThisBatch - 1;

    SET @Created += @ThisBatch;
    SET @Message = CONCAT(N'Tickets generados: ', FORMAT(@Created, N'N0'), N' de ', FORMAT(@ToCreate, N'N0'));
    RAISERROR(@Message, 0, 1) WITH NOWAIT; -- muestra el progreso sin esperar al final del script
END;

-- La SEQUENCE debe seguir después del último Id insertado, o la API intentaría reutilizar Ids.
DECLARE @Restart nvarchar(100) =
    N'ALTER SEQUENCE dbo.TicketIds RESTART WITH ' + CAST((SELECT MAX(Id) + 1 FROM dbo.Tickets) AS nvarchar(20));
EXEC (@Restart);

-- Estadísticas frescas y exactas como punto de partida de las mediciones.
UPDATE STATISTICS dbo.Tickets WITH FULLSCAN;
UPDATE STATISTICS dbo.TicketComments WITH FULLSCAN;

RAISERROR(N'Listo. Distribución resultante (revísala ANTES de medir):', 0, 1) WITH NOWAIT;
SELECT Status, FORMAT(COUNT(*), N'N0') AS Tickets FROM dbo.Tickets GROUP BY Status ORDER BY COUNT(*) DESC;
SELECT ISNULL(AssignedTo, N'(sin asignar)') AS Agente, FORMAT(COUNT(*), N'N0') AS Tickets
FROM dbo.Tickets GROUP BY AssignedTo ORDER BY COUNT(*) DESC;
SELECT Priority, FORMAT(COUNT(*), N'N0') AS Tickets FROM dbo.Tickets GROUP BY Priority ORDER BY COUNT(*) DESC;
SELECT FORMAT(COUNT(*), N'N0') AS Comentarios, CAST(COUNT(*) * 1.0 / (SELECT COUNT(*) FROM dbo.Tickets) AS decimal(4, 2)) AS PromedioPorTicket
FROM dbo.TicketComments;
EXEC sp_spaceused N'dbo.Tickets';
EXEC sp_spaceused N'dbo.TicketComments';
