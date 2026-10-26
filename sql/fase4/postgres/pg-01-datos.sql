-- =====================================================================================
-- Fase 4 · Bloque 5 · PostgreSQL: el mismo HelpDesk, para comparar con SQL Server.
--
-- Crea la base "tuning" (aparte de "helpdesk", que usa el experimento de EF Core con Npgsql) con
-- 1 M de tickets y ~2.5 M de comentarios, con la misma distribución que 01-generar-datos.sql:
-- 80 % Closed, 15 % Resolved, 4 % InProgress, 1 % Open · pedro 30 %, ana 1 %.
-- El "azar" sale de hashtextextended(texto, semilla): siempre se generan los mismos datos.
--
-- Una sola diferencia a propósito: la columna labels text[] (etiquetas), para practicar índices GIN.
-- PostgreSQL tiene arreglos como tipo de columna; en SQL Server esto sería una tabla TicketLabels aparte.
--
-- Uso (desde la raíz del repo):
--   docker cp sql/fase4/postgres/. helpdesk-postgres-1:/tmp/fase4
--   docker exec helpdesk-postgres-1 psql -U helpdesk -d postgres -f /tmp/fase4/pg-01-datos.sql
-- =====================================================================================
\set ON_ERROR_STOP on
\timing on

DROP DATABASE IF EXISTS tuning;
CREATE DATABASE tuning;
\connect tuning

CREATE TABLE tickets (
    id          integer      PRIMARY KEY,
    title       varchar(120) NOT NULL,
    priority    varchar(20)  NOT NULL,
    status      varchar(20)  NOT NULL,
    created_at  timestamptz  NOT NULL,
    assigned_to varchar(50),
    updated_at  timestamptz,
    labels      text[]       NOT NULL DEFAULT '{}'
);

CREATE TABLE ticket_comments (
    id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticket_id  integer       NOT NULL REFERENCES tickets (id) ON DELETE CASCADE,
    author     varchar(50)   NOT NULL,
    body       varchar(2000) NOT NULL,
    created_at timestamptz   NOT NULL
);

\echo 'Generando 1,000,000 de tickets...'
INSERT INTO tickets (id, title, priority, status, created_at, assigned_to, labels)
SELECT
    g.id,
    (ARRAY['No puedo iniciar sesión', 'Impresora sin tóner', 'Error 500 al guardar factura', 'VPN se desconecta',
           'Correo no sincroniza', 'Solicitud de cuenta nueva', 'Pantalla azul al encender', 'Instalar Office',
           'Reporte de ventas lento', 'Cambio de contraseña', 'Wifi de la sala de juntas', 'Disco del servidor lleno'])[1 + d.title_roll],
    CASE WHEN d.priority_roll < 40 THEN 'Low'
         WHEN d.priority_roll < 75 THEN 'Medium'
         WHEN d.priority_roll < 95 THEN 'High'
         ELSE 'Critical' END,
    s.status,
    -- Los abiertos son recientes (últimos 30 días); los cerrados se reparten en 3 años de historia.
    CASE WHEN s.status IN ('Open', 'InProgress')
         THEN now() - make_interval(secs => d.time_roll % (30 * 86400))
         ELSE timestamptz '2023-10-01 00:00:00+00'
              + make_interval(days => (d.time_roll % 1095)::int, secs => d.time_roll % 86400) END,
    CASE WHEN d.agent_roll < 30 THEN 'pedro'     -- 30 %: el agente más cargado
         WHEN d.agent_roll < 50 THEN 'luis'
         WHEN d.agent_roll < 65 THEN 'marta'
         WHEN d.agent_roll < 77 THEN 'sofia'
         WHEN d.agent_roll < 87 THEN 'jorge'
         WHEN d.agent_roll < 94 THEN 'carlos'
         WHEN d.agent_roll < 98 THEN 'elena'
         WHEN d.agent_roll < 99 THEN 'ana' END,  --  1 %; el 1 % restante queda sin asignar (NULL)
    l.labels
FROM generate_series(1, 1000000) AS g(id)
CROSS JOIN LATERAL (
    -- "& 9223372036854775807" deja el hash en positivo: abs() fallaría con el bigint más negativo.
    SELECT (hashtextextended(g.id::text, 1) & 9223372036854775807) % 100          AS status_roll,
           (hashtextextended(g.id::text, 2) & 9223372036854775807) % 100          AS priority_roll,
           (hashtextextended(g.id::text, 3) & 9223372036854775807) % 100          AS agent_roll,
           ((hashtextextended(g.id::text, 4) & 9223372036854775807) % 12)::int    AS title_roll,
           (hashtextextended(g.id::text, 5) & 9223372036854775807) % 2147483647   AS time_roll,
           ((hashtextextended(g.id::text, 6) & 9223372036854775807) % 4)::int     AS label_count
) AS d
CROSS JOIN LATERAL (
    SELECT CASE WHEN d.status_roll < 1  THEN 'Open'
                WHEN d.status_roll < 5  THEN 'InProgress'
                WHEN d.status_roll < 20 THEN 'Resolved'
                ELSE 'Closed' END AS status
) AS s
CROSS JOIN LATERAL (
    -- De 0 a 3 etiquetas comunes (cada una en ~12 % de los tickets) y una rara, "auditoria", en 1 de cada 5,000.
    SELECT ARRAY(
               SELECT DISTINCT (ARRAY['vpn', 'correo', 'impresora', 'sap', 'office', 'wifi', 'cuenta', 'servidor',
                                      'factura', 'pantalla', 'red', 'licencia'])
                               [1 + ((hashtextextended(g.id::text || '-' || n, 7) & 9223372036854775807) % 12)::int]
               FROM generate_series(1, d.label_count) AS n)
           || CASE WHEN g.id % 5000 = 0 THEN ARRAY['auditoria'] ELSE ARRAY[]::text[] END AS labels
) AS l;

\echo 'Generando los comentarios (de 0 a 5 por ticket)...'
INSERT INTO ticket_comments (ticket_id, author, body, created_at)
SELECT
    t.id,
    (ARRAY['pedro', 'luis', 'marta', 'sistema'])[1 + (c.h % 4)::int],
    (ARRAY['Le pedí al usuario una captura del error.', 'Lo reproduje en el ambiente de pruebas.',
           'Escalado con el proveedor; esperamos respuesta.', 'Aplicada la solución, pendiente de confirmar.',
           'El usuario confirma que ya funciona.'])[1 + ((c.h / 4) % 5)::int],
    t.created_at + make_interval(hours => k.n * 3)
FROM tickets AS t
CROSS JOIN LATERAL generate_series(1, ((hashtextextended(t.id::text, 8) & 9223372036854775807) % 6)::int) AS k(n)
CROSS JOIN LATERAL (SELECT hashtextextended(t.id::text || '/' || k.n, 9) & 9223372036854775807 AS h) AS c;

\echo 'Índices: los mismos que en SQL Server'
CREATE INDEX ix_tickets_status_priority ON tickets (status, priority);
CREATE INDEX ix_tickets_assigned_to ON tickets (assigned_to) INCLUDE (status);
-- Índice parcial: el equivalente del índice filtrado de SQL Server (caso 2).
CREATE INDEX ix_tickets_open_assigned_to ON tickets (assigned_to) INCLUDE (status, priority, created_at)
    WHERE status IN ('Open', 'InProgress');
CREATE INDEX ix_ticket_comments_ticket_id_created_at ON ticket_comments (ticket_id, created_at);

-- ANALYZE calcula las estadísticas (como UPDATE STATISTICS). VACUUM, además, marca qué páginas tienen todas
-- sus filas visibles para todos: sin ese "mapa de visibilidad", un Index Only Scan igual tiene que ir a la tabla.
VACUUM ANALYZE tickets;
VACUUM ANALYZE ticket_comments;

\echo 'Listo. Distribución resultante:'
SELECT status, count(*) AS tickets FROM tickets GROUP BY status ORDER BY count(*) DESC;
SELECT coalesce(assigned_to, '(sin asignar)') AS agente, count(*) AS tickets FROM tickets GROUP BY assigned_to ORDER BY count(*) DESC;
SELECT count(*) AS comentarios, round(count(*)::numeric / 1000000, 2) AS promedio_por_ticket FROM ticket_comments;
SELECT count(*) FILTER (WHERE labels @> ARRAY['auditoria']) AS con_auditoria,
       count(*) FILTER (WHERE labels @> ARRAY['vpn'])       AS con_vpn
FROM tickets;
SELECT relname AS tabla,
       pg_size_pretty(pg_relation_size(oid))       AS datos,
       pg_size_pretty(pg_indexes_size(oid))        AS indices
FROM pg_class WHERE relname IN ('tickets', 'ticket_comments');
