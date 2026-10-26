/* =====================================================================================
   Fase 4 · Bloque 4 · Estadísticas de espera: en qué se le va el tiempo a SQL Server.

   Cada vez que una tarea no puede avanzar (espera un bloqueo, el disco, la CPU, la red...), SQL Server
   anota cuánto esperó y por qué. sys.dm_os_wait_stats acumula desde el último reinicio, así que lo
   que sirve es la DIFERENCIA entre dos fotos tomadas mientras ocurre el problema.

     1. Foto, @Seconds segundos de espera, segunda foto: las esperas de ese intervalo.
     2. Lo acumulado desde que arrancó el servidor, para comparar.

   Se excluyen las esperas "de fondo": procesos internos que duermen a propósito y que, sumadas,
   siempre encabezan la lista sin significar nada (también WAITFOR, que es una pausa pedida).
   La columna Senal_ms es el tiempo que la tarea ya tenía lo que esperaba, pero aguardaba turno de CPU:
   si es una parte grande del total, falta CPU.

   Uso: córrelo y, mientras cuenta, reproduce el problema en otra ventana.
     sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d HelpDesk -C -I -f 65001 -W -i sql\fase4\10-esperas.sql
   ===================================================================================== */
SET NOCOUNT ON;

DECLARE @Seconds int = 20;

DECLARE @Background TABLE (wait_type nvarchar(60) PRIMARY KEY);
INSERT INTO @Background VALUES
    (N'BROKER_EVENTHANDLER'), (N'BROKER_RECEIVE_WAITFOR'), (N'BROKER_TASK_STOP'), (N'BROKER_TO_FLUSH'),
    (N'BROKER_TRANSMITTER'), (N'CHECKPOINT_QUEUE'), (N'CHKPT'), (N'CLR_AUTO_EVENT'), (N'CLR_MANUAL_EVENT'),
    (N'CLR_SEMAPHORE'), (N'DIRTY_PAGE_POLL'), (N'DISPATCHER_QUEUE_SEMAPHORE'), (N'EXECSYNC'), (N'FSAGENT'),
    (N'FT_IFTS_SCHEDULER_IDLE_WAIT'), (N'FT_IFTSHC_MUTEX'), (N'HADR_FILESTREAM_IOMGR_IOCOMPLETION'),
    (N'HADR_LOGCAPTURE_WAIT'), (N'HADR_NOTIFICATION_DEQUEUE'), (N'HADR_TIMER_TASK'), (N'HADR_WORK_QUEUE'),
    (N'KSOURCE_WAKEUP'), (N'LAZYWRITER_SLEEP'), (N'LOGMGR_QUEUE'), (N'MEMORY_ALLOCATION_EXT'),
    (N'ONDEMAND_TASK_QUEUE'), (N'PARALLEL_REDO_DRAIN_WORKER'), (N'PARALLEL_REDO_LOG_CACHE'),
    (N'PARALLEL_REDO_TRAN_LIST'), (N'PARALLEL_REDO_WORKER_SYNC'), (N'PARALLEL_REDO_WORKER_WAIT_WORK'),
    (N'PREEMPTIVE_OS_FLUSHFILEBUFFERS'), (N'PREEMPTIVE_XE_GETTARGETSTATE'), (N'PVS_PREALLOCATE'),
    (N'PWAIT_ALL_COMPONENTS_INITIALIZED'), (N'PWAIT_DIRECTLOGCONSUMER_GETNEXT'),
    (N'PWAIT_EXTENSIBILITY_CLEANUP_TASK'), (N'QDS_ASYNC_QUEUE'),
    (N'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP'), (N'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP'),
    (N'QDS_SHUTDOWN_QUEUE'), (N'REDO_THREAD_PENDING_WORK'), (N'REQUEST_FOR_DEADLOCK_SEARCH'),
    (N'RESOURCE_QUEUE'), (N'SERVER_IDLE_CHECK'), (N'SLEEP_BPOOL_FLUSH'), (N'SLEEP_DBSTARTUP'),
    (N'SLEEP_DCOMSTARTUP'), (N'SLEEP_MASTERDBREADY'), (N'SLEEP_MASTERMDREADY'), (N'SLEEP_MASTERUPGRADED'),
    (N'SLEEP_MSDBSTARTUP'), (N'SLEEP_SYSTEMTASK'), (N'SLEEP_TASK'), (N'SLEEP_TEMPDBSTARTUP'),
    (N'SNI_HTTP_ACCEPT'), (N'SOS_WORK_DISPATCHER'), (N'SP_SERVER_DIAGNOSTICS_SLEEP'),
    (N'SQLTRACE_BUFFER_FLUSH'), (N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP'), (N'SQLTRACE_WAIT_ENTRIES'),
    (N'VDI_CLIENT_OTHER'), (N'WAIT_FOR_RESULTS'), (N'WAITFOR'), (N'WAITFOR_TASKSHUTDOWN'),
    (N'WAIT_XTP_CKPT_CLOSE'), (N'WAIT_XTP_HOST_WAIT'), (N'WAIT_XTP_OFFLINE_CKPT_NEW_LOG'),
    (N'WAIT_XTP_RECOVERY'), (N'XE_DISPATCHER_JOIN'), (N'XE_DISPATCHER_WAIT'), (N'XE_LIVE_TARGET_TVF'),
    (N'XE_TIMER_EVENT');

DECLARE @Before TABLE (wait_type nvarchar(60) PRIMARY KEY, waiting_tasks_count bigint, wait_time_ms bigint, signal_wait_time_ms bigint);
INSERT INTO @Before
SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
FROM sys.dm_os_wait_stats;

DECLARE @Delay char(8) = CONVERT(char(8), DATEADD(SECOND, @Seconds, 0), 108);
RAISERROR(N'Midiendo esperas durante %d segundos...', 0, 1, @Seconds) WITH NOWAIT;
WAITFOR DELAY @Delay;

PRINT N'### 1. Esperas del intervalo';
WITH Delta AS (
    SELECT
        w.wait_type,
        w.waiting_tasks_count - b.waiting_tasks_count AS Tasks,
        w.wait_time_ms - b.wait_time_ms               AS WaitMs,
        w.signal_wait_time_ms - b.signal_wait_time_ms AS SignalMs
    FROM sys.dm_os_wait_stats AS w
    JOIN @Before AS b ON b.wait_type = w.wait_type
    WHERE w.wait_type NOT IN (SELECT wait_type FROM @Background)
)
SELECT TOP (10)
    wait_type                                                             AS Espera,
    Tasks                                                                 AS Veces,
    WaitMs                                                                AS Total_ms,
    SignalMs                                                              AS Senal_ms,
    CAST(100.0 * WaitMs / NULLIF(SUM(WaitMs) OVER (), 0) AS decimal(5, 1)) AS Porcentaje,
    CASE
        WHEN wait_type LIKE N'LCK_M_%XACT%'      THEN N'Bloqueo (optimized locking): espera a que termine la transacción que modificó la fila'
        WHEN wait_type LIKE N'LCK_M_%'           THEN N'Bloqueo: espera a que otra transacción suelte una fila, página o tabla'
        WHEN wait_type LIKE N'PAGEIOLATCH_%'     THEN N'Disco: leer una página de datos a memoria'
        WHEN wait_type = N'WRITELOG'             THEN N'Disco: escribir el log al confirmar (COMMIT)'
        WHEN wait_type = N'LOGBUFFER'            THEN N'Log: espera lugar en el búfer de log en memoria'
        WHEN wait_type = N'SEQUENCE_GENERATION'  THEN N'Generar valores de una SEQUENCE (al agotar los que tenía en caché)'
        WHEN wait_type LIKE N'CX%'               THEN N'Paralelismo: hilos de una consulta esperando a los demás'
        WHEN wait_type = N'SOS_SCHEDULER_YIELD'  THEN N'CPU: la tarea cedió su turno y espera volver a ejecutar'
        WHEN wait_type = N'ASYNC_NETWORK_IO'     THEN N'Red/cliente: la app no lee los resultados tan rápido como llegan'
        WHEN wait_type = N'RESOURCE_SEMAPHORE'   THEN N'Memoria: la consulta espera que le concedan memoria'
        WHEN wait_type = N'THREADPOOL'           THEN N'No quedan hilos libres para atender peticiones'
        WHEN wait_type LIKE N'PAGELATCH_%'       THEN N'Contención sobre una página que ya está en memoria'
        WHEN wait_type LIKE N'%IO_COMPLETION'    THEN N'Disco: otras lecturas o escrituras (no de páginas de datos)'
        WHEN wait_type LIKE N'LATCH_%'           THEN N'Estructura interna en memoria; en planes paralelos, el reparto del trabajo entre hilos'
        WHEN wait_type LIKE N'PREEMPTIVE_OS_%'   THEN N'Llamada al sistema operativo (abrir conexiones cifradas, escribir archivos...)'
        ELSE N''
    END                                                                   AS QueSignifica
FROM Delta
WHERE WaitMs > 0
ORDER BY WaitMs DESC;

PRINT N'### 2. Acumulado desde el arranque del servidor';
SELECT TOP (10)
    w.wait_type                                                           AS Espera,
    w.waiting_tasks_count                                                 AS Veces,
    w.wait_time_ms                                                        AS Total_ms,
    w.signal_wait_time_ms                                                 AS Senal_ms,
    CAST(100.0 * w.wait_time_ms / SUM(w.wait_time_ms) OVER () AS decimal(5, 1)) AS Porcentaje
FROM sys.dm_os_wait_stats AS w
WHERE w.wait_type NOT IN (SELECT wait_type FROM @Background)
  AND w.wait_time_ms > 0
ORDER BY w.wait_time_ms DESC;
