-- =====================================================================================
-- Fase 4 · Bloque 5 · PostgreSQL: los planes de las mismas consultas que en SQL Server.
--
-- EXPLAIN muestra el plan elegido. Con ANALYZE además ejecuta la consulta y muestra filas y tiempos reales;
-- con BUFFERS, cuántas páginas de 8 KB tocó: "shared hit" = ya estaban en memoria, "read" = del disco.
-- Es el equivalente del plan real + SET STATISTICS IO de SQL Server.
--
-- Uso: docker exec helpdesk-postgres-1 psql -U helpdesk -d tuning -f /tmp/fase4/pg-02-planes.sql
--      (antes: pg-01-datos.sql y el docker cp que indica)
-- =====================================================================================
\set ON_ERROR_STOP on
\connect tuning
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
SELECT pg_stat_statements_reset() IS NOT NULL AS estadisticas_reiniciadas;

\echo '### Q1 · página de tickets Open + High (SQL Server: 449 lecturas, o 172 con el estimador clásico)'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, assigned_to, created_at, priority, status, title, updated_at
FROM tickets WHERE status = 'Open' AND priority = 'High' ORDER BY id LIMIT 51;

\echo '### Q2 · página por agente como sentencia preparada'
-- Una sentencia preparada se planifica "a la medida" de los valores las primeras 5 veces. Después,
-- PostgreSQL compara: si el plan genérico (que no mira los valores) no parece más caro que el promedio
-- de los planes a la medida, se queda con él. Es su versión del parameter sniffing (caso 6).
-- Npgsql (el proveedor de EF Core para PostgreSQL) solo prepara si se pide: Max Auto Prepare en la conexión.
PREPARE agent_page(varchar, int) AS
    SELECT id, assigned_to, created_at, priority, status, title, updated_at
    FROM tickets WHERE assigned_to = $1 ORDER BY id LIMIT $2;
\echo '--- a la medida, pedro'
EXPLAIN (ANALYZE, BUFFERS) EXECUTE agent_page('pedro', 51);
\echo '--- a la medida, un agente sin tickets'
EXPLAIN (ANALYZE, BUFFERS) EXECUTE agent_page('nadie', 51);
\echo '--- modo por defecto (auto): 6 ejecuciones más, alternando agentes. ¿Cuántas usaron el plan genérico?'
EXECUTE agent_page('ana', 51) \g /dev/null
EXECUTE agent_page('luis', 51) \g /dev/null
EXECUTE agent_page('pedro', 51) \g /dev/null
EXECUTE agent_page('nadie', 51) \g /dev/null
EXECUTE agent_page('pedro', 51) \g /dev/null
EXECUTE agent_page('nadie', 51) \g /dev/null
SELECT name, generic_plans, custom_plans FROM pg_prepared_statements WHERE name = 'agent_page';
\echo '--- forzando el plan genérico: el del agente "promedio" (como OPTIMIZE FOR UNKNOWN en SQL Server)'
SET plan_cache_mode = force_generic_plan;
EXPLAIN (ANALYZE, BUFFERS) EXECUTE agent_page('nadie', 51);
RESET plan_cache_mode;
DEALLOCATE agent_page;

\echo '### Q4 · dashboard con el índice parcial (SQL Server: 409 lecturas)'
EXPLAIN (ANALYZE, BUFFERS)
SELECT coalesce(assigned_to, '(sin asignar)')                        AS agent,
       count(*)                                                      AS open_tickets,
       count(*) FILTER (WHERE priority = 'Critical')                 AS critical_tickets,
       floor(extract(epoch FROM now() - min(created_at)) / 3600)     AS oldest_open_hours
FROM tickets
WHERE status IN ('Open', 'InProgress')
GROUP BY assigned_to
ORDER BY critical_tickets DESC, open_tickets DESC;

\echo '### Etiquetas: la rara ("auditoria", 200 tickets) sin índice'
DROP INDEX IF EXISTS ix_tickets_labels;   -- el script se puede repetir: el índice se crea más abajo
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, title FROM tickets WHERE labels @> ARRAY['auditoria'] ORDER BY id LIMIT 51;

\echo '### La misma, con un índice GIN (un índice invertido: de cada etiqueta, a los tickets que la tienen)'
CREATE INDEX ix_tickets_labels ON tickets USING gin (labels);
ANALYZE tickets;
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, title FROM tickets WHERE labels @> ARRAY['auditoria'] ORDER BY id LIMIT 51;

\echo '### Una etiqueta común ("vpn", 12 %): aun con el índice, conviene recorrer la tabla'
EXPLAIN (ANALYZE, BUFFERS)
SELECT id, title FROM tickets WHERE labels @> ARRAY['vpn'] ORDER BY id LIMIT 51;

\echo '### pg_stat_statements (el "Query Store" de PostgreSQL): lo que corrió en esta base, por tiempo total'
SELECT calls                                                AS llamadas,
       round(total_exec_time::numeric, 1)                   AS total_ms,
       round(mean_exec_time::numeric, 2)                    AS promedio_ms,
       shared_blks_hit + shared_blks_read                   AS paginas,
       left(regexp_replace(query, '\s+', ' ', 'g'), 80)     AS consulta
FROM pg_stat_statements
WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
  AND query NOT ILIKE '%pg_stat_statements%'
ORDER BY total_exec_time DESC
LIMIT 8;
