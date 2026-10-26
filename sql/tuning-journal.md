# Diario de tuning — HelpDesk (Fase 4)

Registro de consultas lentas: cómo se midieron, qué mostraba el plan, qué se cambió y cuánto mejoró.

## Entorno y método

| | |
|---|---|
| Motor | SQL Server 2025 RTM-CU9 (17.0.5005.3), Developer Edition, en Docker |
| Recursos | 12 CPUs lógicas, `MSSQL_MEMORY_LIMIT_MB = 4096`, datos en disco **HDD** |
| Datos | 1,000,000 tickets (≈ 225 MB) y 2,499,518 comentarios (≈ 440 MB), generados con [`fase4/01-generar-datos.sql`](fase4/01-generar-datos.sql) |
| Distribución | 80 % `Closed`, 15 % `Resolved`, 4 % `InProgress`, 1 % `Open` · pedro 30 % de los tickets, ana 1 % |
| Consultas | Copia exacta del SQL que generan EF Core y Dapper, ejecutado con `sp_executesql`, igual que desde la app ([`fase4/02-linea-base.sql`](fase4/02-linea-base.sql)) |
| Métrica principal | **Lecturas lógicas** (`SET STATISTICS IO`): no dependen de la caché ni del disco, así que son comparables entre corridas |
| Métricas secundarias | CPU y tiempo transcurrido (`SET STATISTICS TIME`), con la caché ya caliente (segunda corrida) |
| Planes | Planes reales con `SET STATISTICS XML ON`, resumidos con [`fase4/Resumir-Planes.ps1`](fase4/Resumir-Planes.ps1) |

---

## Línea base (antes de tocar nada)

| # | Consulta (endpoint) | Filas | Lecturas lógicas | CPU | Transcurrido | Plan |
|---|---|---:|---:|---:|---:|---|
| Q1 | `GET /api/tickets?status=Open&priority=High` | 1,996 | 17,823 | 282 ms | 29 ms | Clustered Index Scan paralelo (toda la tabla) |
| Q2a | `GET /api/tickets?assignedTo=ana` | 10,165 | 17,823 | 301 ms | 38 ms | Clustered Index Scan paralelo |
| Q2b | `GET /api/tickets?assignedTo=pedro` | 300,214 | 17,823 | 457 ms | 1,066 ms | Clustered Index Scan paralelo, **con el plan de ana** |
| Q3 | `GET /api/tickets/activity?latestComments=3` | 108,482 | 74,605 | **4,771 ms** | 1,169 ms | Sort de 2.5 M de comentarios + Window Aggregate · **590 MB de memoria** |
| Q4 | `GET /api/dashboard` (carga por agente) | 9 | 17,823 | 397 ms | 38 ms | Clustered Index Scan paralelo |

17,823 lecturas = **todas las páginas de `Tickets`**: las cinco consultas leen la tabla completa.

### Observaciones

1. **Ninguna usa los índices que creamos en la Fase 3** (`IX_Tickets_Status_Priority`, `IX_Tickets_AssignedTo`). Para devolver todas las columnas del ticket, usarlos implicaría un *key lookup* por fila. A partir de cierto número de filas (el *tipping point*), recorrer la tabla le sale más barato al optimizador.
2. **Q1: la estimación falla.** Estimó 10,037 filas (las de `Status = 'Open'`, como si el filtro por prioridad no redujera nada) y fueron 1,996. Con la estimación correcta, buscar en el índice habría ganado. *Pendiente investigar (bloque de estadísticas).*
3. **Q2b reutiliza el plan cacheado de Q2a.** El plan se compiló para `ana` (10 mil filas) y se reutilizó para `pedro` (300 mil). Aquí da igual, porque los dos recorren la tabla, pero es la semilla del **parameter sniffing**.
4. **Q3 es la peor**, y es la consulta "optimizada" del N+1 de la Fase 3: `ROW_NUMBER() OVER (PARTITION BY TicketId ...)` sobre **todos** los comentarios obliga a ordenar 2.5 millones de filas para quedarse con 100 mil. Pide 590 MB de memoria: con 4 GB para toda la instancia, **6 ejecuciones simultáneas la agotarían**.
5. **Todo va en paralelo (DOP 12)**, incluso Q1 con un costo de 13.4. El umbral por defecto (`cost threshold for parallelism = 5`) viene de los años 90, y por eso una consulta de 2 mil filas gasta 282 ms de CPU repartidos en 12 hilos.
6. **Los índices que SQL Server "echa de menos"** incluyen casi todas las columnas, porque las consultas piden la entidad completa (`SELECT` de todas las columnas). Son pistas, no órdenes: crearlos tal cual duplicaría la tabla.

### Hipótesis para los siguientes bloques

- Índices que **cubran** las consultas frecuentes, y consultas que pidan **solo las columnas que necesitan** (proyecciones).
- Reescribir Q3 para que numere solo los comentarios de los tickets abiertos (`OUTER APPLY ... TOP (n)`), y **paginar**: hoy el endpoint devuelve 50 mil tickets en una sola respuesta.
- Investigar la estimación de Q1 (estadísticas) y el plan compartido de Q2a/Q2b (*parameter sniffing*).
- Revisar `cost threshold for parallelism` y `MAXDOP`.

---

## Casos

*(Se irán agregando: problema → plan antes → cambio → plan después → mejora medida.)*
