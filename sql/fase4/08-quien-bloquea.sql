/* =====================================================================================
   Fase 4 · Bloque 4 · ¿Quién bloquea a quién? Una foto del momento.

   Ejecútalo en otra ventana mientras algo "se queda colgado":
     1. Sesiones que esperan un bloqueo: qué esperan, desde hace cuánto y en qué sentencia.
     2. Bloqueadores raíz: los que bloquean sin estar bloqueados. Suele ser una transacción que sigue
        abierta (la app la abrió y todavía no hace COMMIT), aunque en ese momento no esté ejecutando nada.
     3. Bloqueos de cada sesión en esta base, agrupados por tabla, índice y modo.
     4. Escalamientos de bloqueo acumulados por índice (desde que la tabla se cargó en memoria).

   Modos más comunes: S = leer · U = leer para modificar · X = modificar · IS/IX = "tengo o voy a pedir
   S/X más abajo" (en la página o la fila) · XACT = bloqueo de transacción del optimized locking (SQL 2025).

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\08-quien-bloquea.sql
   ===================================================================================== */
SET NOCOUNT ON;

PRINT N'### 1. Sesiones esperando un bloqueo';
SELECT
    r.session_id                AS Sesion,
    r.blocking_session_id       AS BloqueadaPor,
    r.wait_type                 AS TipoDeEspera,
    r.wait_time                 AS Espera_ms,
    r.wait_resource             AS Recurso,
    LEFT(REPLACE(REPLACE(SUBSTRING(st.text, r.statement_start_offset / 2 + 1,
        (IIF(r.statement_end_offset = -1, DATALENGTH(st.text), r.statement_end_offset) - r.statement_start_offset) / 2 + 1),
        NCHAR(13), N''), NCHAR(10), N' '), 120) AS Sentencia
FROM sys.dm_exec_requests AS r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS st
WHERE r.blocking_session_id > 0
ORDER BY r.wait_time DESC;

PRINT N'### 2. Bloqueadores raíz (bloquean a otros y nadie los bloquea a ellos)';
-- BloqueadaPor apunta al de ADELANTE en la cola, no siempre al culpable: los bloqueos se atienden en orden
-- de llegada, y quien llega detrás de alguien que espera, espera también. Se sube por la cadena hasta la raíz.
WITH Waiting AS (
    SELECT session_id, blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id > 0
),
Chain AS (
    SELECT session_id, blocking_session_id AS Blocker, 1 AS Depth FROM Waiting
    UNION ALL
    SELECT c.session_id, w.blocking_session_id, c.Depth + 1
    FROM Chain AS c
    JOIN Waiting AS w ON w.session_id = c.Blocker
    WHERE c.Depth < 50   -- un deadlock es un ciclo: sin este tope, la recursión no terminaría
)
SELECT
    s.session_id                                                          AS Sesion,
    s.status                                                              AS Estado,
    s.host_name                                                           AS Equipo,
    s.program_name                                                        AS Programa,
    (SELECT COUNT(DISTINCT c.session_id) FROM Chain AS c WHERE c.Blocker = s.session_id) AS SesionesQueBloquea,
    DATEDIFF(SECOND, t.transaction_begin_time, SYSDATETIME())             AS SegundosConLaTransaccionAbierta,
    LEFT(REPLACE(REPLACE(st.text, NCHAR(13), N''), NCHAR(10), N' '), 120) AS UltimoLote
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_tran_session_transactions AS tst ON tst.session_id = s.session_id
LEFT JOIN sys.dm_tran_active_transactions AS t ON t.transaction_id = tst.transaction_id
LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS st
WHERE s.session_id IN (SELECT blocking_session_id FROM Waiting)
  AND s.session_id NOT IN (SELECT session_id FROM Waiting);

PRINT N'### 3. Bloqueos por sesión en esta base';
SELECT
    l.request_session_id                                                  AS Sesion,
    l.resource_type                                                       AS Recurso,
    l.request_mode                                                        AS Modo,
    l.request_status                                                      AS Estado,
    COALESCE(OBJECT_NAME(CAST(IIF(l.resource_type = N'OBJECT', l.resource_associated_entity_id, p.object_id) AS int)), N'') AS Tabla,
    COALESCE(i.name, N'')                                                 AS Indice,
    COUNT(*)                                                              AS Cantidad
FROM sys.dm_tran_locks AS l
JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id AND s.is_user_process = 1   -- sin procesos internos (Query Store, etc.)
LEFT JOIN sys.partitions AS p
    ON p.hobt_id = l.resource_associated_entity_id AND l.resource_type IN (N'KEY', N'PAGE', N'RID', N'HOBT')
LEFT JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
WHERE l.resource_database_id = DB_ID()
  AND l.request_session_id <> @@SPID
  AND l.resource_type <> N'DATABASE'   -- toda sesión conectada tiene un S sobre la base: no aporta
GROUP BY l.request_session_id, l.resource_type, l.request_mode, l.request_status,
         IIF(l.resource_type = N'OBJECT', l.resource_associated_entity_id, p.object_id), i.name
ORDER BY Sesion, Estado DESC, Cantidad DESC;

PRINT N'### 4. Escalamientos de bloqueo y esperas por índice (acumulado)';
SELECT
    OBJECT_NAME(s.object_id)                AS Tabla,
    i.name                                  AS Indice,
    s.index_lock_promotion_attempt_count    AS IntentosDeEscalar,
    s.index_lock_promotion_count            AS Escalamientos,
    s.row_lock_wait_count                   AS EsperasPorFila,
    s.row_lock_wait_in_ms                   AS EsperaPorFila_ms,
    s.page_lock_wait_count                  AS EsperasPorPagina,
    s.page_lock_wait_in_ms                  AS EsperaPorPagina_ms
FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS s
JOIN sys.indexes AS i ON i.object_id = s.object_id AND i.index_id = s.index_id
WHERE OBJECTPROPERTY(s.object_id, 'IsUserTable') = 1
  AND (s.index_lock_promotion_attempt_count > 0 OR s.row_lock_wait_count > 0 OR s.page_lock_wait_count > 0)
ORDER BY s.index_lock_promotion_count DESC, s.row_lock_wait_in_ms + s.page_lock_wait_in_ms DESC;
