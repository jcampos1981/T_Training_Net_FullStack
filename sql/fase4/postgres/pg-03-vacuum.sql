-- =====================================================================================
-- Fase 4 · Bloque 5 · PostgreSQL: versiones muertas, HOT y VACUUM.
--
-- En PostgreSQL un UPDATE no sobrescribe la fila: escribe una versión nueva y deja la vieja "muerta"
-- hasta que VACUUM la limpia. Así funciona su MVCC: quien lee ve la versión que le corresponde, sin
-- bloqueos (lo que en SQL Server hace RCSI con el version store, y ADR con versiones dentro de la base).
--
--   1. La reasignación de pedro (15 mil filas) con ROLLBACK: ¿qué queda en la tabla?
--   2. Un UPDATE que no toca columnas indexadas: puede ser HOT (heap-only tuple), sin tocar los índices.
--   3. VACUUM y cuándo lo haría solo autovacuum.
--   4. El mismo UPDATE del paso 2 con fillfactor 90 (10 % libre en cada página).
--
-- Uso: docker exec helpdesk-postgres-1 psql -U helpdesk -d tuning -f /tmp/fase4/pg-03-vacuum.sql
-- =====================================================================================
\set ON_ERROR_STOP on
\timing on
\connect tuning

-- Las estadísticas de actividad se publican cada tanto: esto las fuerza para que la siguiente consulta las vea.
-- (Al reiniciar los contadores, "vivas" también queda en 0 hasta el siguiente VACUUM o ANALYZE.)
\set stats 'SELECT pg_stat_force_next_flush(); SELECT n_live_tup AS vivas, n_dead_tup AS muertas, n_tup_upd AS actualizadas, n_tup_hot_upd AS hot FROM pg_stat_user_tables WHERE relname = ''tickets'';'

\echo '### 0. Punto de partida: páginas llenas, como después de la carga (el paso 4 las deja al 90 %)'
ALTER TABLE tickets SET (fillfactor = 100);
VACUUM FULL tickets;      -- reescribe la tabla completa: bloquea todo mientras dura
VACUUM ANALYZE tickets;
SELECT pg_stat_reset_single_table_counters('tickets'::regclass) IS NOT NULL AS contadores_en_cero;
:stats

\echo '### 1. Reasignación de pedro a luis (15 mil tickets abiertos) y ROLLBACK'
BEGIN;
UPDATE tickets SET assigned_to = 'luis', updated_at = now()
WHERE assigned_to = 'pedro' AND status IN ('Open', 'InProgress');
ROLLBACK;
:stats
\echo 'El ROLLBACK fue instantáneo: no hay nada que deshacer, las versiones nuevas simplemente quedaron muertas.'

\echo '### 2. Mismo filtro, cambiando solo updated_at (ninguna columna indexada)'
BEGIN;
UPDATE tickets SET updated_at = now()
WHERE assigned_to = 'pedro' AND status IN ('Open', 'InProgress');
ROLLBACK;
:stats
\echo 'HOT solo es posible si la versión nueva cabe en la misma página. Después de una carga masiva las páginas'
\echo 'están llenas (fillfactor 100): por eso en tablas que se actualizan mucho conviene dejar espacio (fillfactor 80-90).'

\echo '### 3. VACUUM limpia las versiones muertas (y actualiza el mapa de visibilidad)'
VACUUM (VERBOSE) tickets;
:stats

\echo '### autovacuum: limpia solo cuando las muertas superan threshold + scale_factor x filas'
SELECT current_setting('autovacuum_vacuum_threshold')::int
       + current_setting('autovacuum_vacuum_scale_factor')::numeric * (SELECT reltuples FROM pg_class WHERE relname = 'tickets')
       AS muertas_para_que_actue_autovacuum;

\echo '### 4. fillfactor 90: cada página deja 10 % libre para las versiones nuevas'
ALTER TABLE tickets SET (fillfactor = 90);
VACUUM FULL tickets;
VACUUM ANALYZE tickets;
SELECT pg_size_pretty(pg_relation_size('tickets')) AS tabla;
SELECT pg_stat_reset_single_table_counters('tickets'::regclass) IS NOT NULL AS contadores_en_cero;
BEGIN;
UPDATE tickets SET updated_at = now()
WHERE assigned_to = 'pedro' AND status IN ('Open', 'InProgress');
ROLLBACK;
:stats
VACUUM tickets;
