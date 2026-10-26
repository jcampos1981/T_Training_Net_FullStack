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
| Opciones de sesión | `sqlcmd -I` (`QUOTED_IDENTIFIER ON`), igual que la app. **Sin `-I` se ignoran los índices filtrados** (ver caso 2) |

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

Mediciones del "después" con [`fase4/03-despues-bloque2.sql`](fase4/03-despues-bloque2.sql).

### Caso 1 · Paginación keyset en `GET /api/tickets` (Q1, Q2a, Q2b)

**Problema.** Los endpoints devolvían **todas** las filas del filtro (300,214 para pedro). Con tantas filas, cualquier índice que no cubra la consulta pierde contra recorrer la tabla (*tipping point*).

**Cambio.** Paginación por keyset: `TOP (51) ... WHERE Id > @after ORDER BY Id`. Se pide una fila de más para saber si hay otra página, y la respuesta trae `nextAfter`. `pageSize` va de 1 a 100.

| | Filas devueltas | Lecturas antes → después | CPU antes → después |
|---|---:|---:|---:|
| Q1 Open + High | 1,996 → 51 | 17,823 → **449** | 282 → 4 ms |
| Q2a ana | 10,165 → 51 | 17,823 → **96** | 301 → 1 ms |
| Q2b pedro | 300,214 → 51 | 17,823 → **19** | 457 → 0 ms |

**Plan después.** Clustered Index Scan **en orden de Id con *row goal***: se detiene al juntar 51 filas. Cuanto más común es el valor buscado, menos lee (pedro: 19 páginas).

**Keyset vs. `OFFSET`** (la misma página profunda de pedro):

| | Lecturas | CPU |
|---|---:|---:|
| Q2c keyset (`Id > 950000`) | **9** | 0 ms |
| Q2d `OFFSET 285000 ROWS` | 16,681 | 173 ms |

`OFFSET` lee y descarta todas las filas anteriores: la página 5,700 cuesta 5,700 veces la primera. Keyset salta directo con un *seek*.

**Pendiente.** El *row goal* supone que las filas buscadas están repartidas de forma pareja. Para un valor **raro o inexistente** (un agente sin tickets) recorrería la tabla entera buscando 51 filas. A revisar en el bloque de estadísticas.

### Caso 2 · Índice filtrado para el dashboard (Q4)

**Problema.** `WHERE Status IN ('Open', 'InProgress')` necesita el 5 % de la tabla, pero no había ningún índice útil para ese filtro: recorría 1 M de filas.

**Cambio.** `IX_Tickets_Open_AssignedTo (AssignedTo) INCLUDE (Status, Priority, CreatedAt) WHERE Status IN (N'Open', N'InProgress')`. Contiene solo ~50 mil filas y **cubre** la consulta, así que no hace *key lookups*. Se creó con una migración de EF Core (`HasFilter` + `IncludeProperties`).

| | Lecturas | CPU | Paralelismo |
|---|---:|---:|---|
| Antes | 17,823 | 397 ms | DOP 12 |
| Después | **409** | **13 ms** | DOP 1 |

⚠️ **Trampa:** la primera medición dio 17,823 otra vez. `sqlcmd` se conecta con `QUOTED_IDENTIFIER OFF`, y con esa opción **SQL Server no puede usar índices filtrados** (tampoco vistas indexadas ni índices sobre columnas calculadas). La app (SqlClient) se conecta con `ON`. Es el origen del clásico "en SSMS es rápido y en la app lento", o al revés: **opciones de sesión distintas producen planes distintos**.

Además, el índice filtrado solo se usa si la consulta trae **el mismo filtro como literal**. Con `Status = @status` no sirve, porque el optimizador no puede garantizar al compilar que el valor caiga dentro del filtro.

### Caso 3 · Actividad de tickets abiertos reescrita (Q3)

**Problema.** La proyección de EF Core (Fase 3) generaba `ROW_NUMBER() OVER (PARTITION BY TicketId ...)` sobre **todos** los comentarios. Ordenaba 2.5 M de filas para quedarse con 100 mil, pedía 590 MB de memoria y devolvía 50 mil tickets en una sola respuesta.

**Cambio.** SQL a mano con Dapper, en dos pasos:
1. La página de tickets abiertos (`TOP (51)` por keyset).
2. Solo para esos tickets, `CROSS APPLY` para el conteo y `OUTER APPLY (SELECT TOP (@n) ... ORDER BY CreatedAt DESC)` para los últimos comentarios.

| | Lecturas | CPU | Memoria | Filas |
|---|---:|---:|---:|---:|
| Antes | 74,605 | 4,771 ms | 590 MB | 108,482 |
| Después | **697** | **2 ms** | **0** | 114 |

**Plan después.** *Nested Loops* con *seeks*: 51 tickets; por cada uno, un *seek* para el conteo y otro (+ *key lookup*) para los comentarios, usando `IX_TicketComments_TicketId_CreatedAt`.

**Lección.** EF Core resolvió el N+1, pero la **forma** del SQL que genera para "los N más recientes por grupo" no escala. En una ruta de lectura caliente, escribir el SQL a mano es la decisión correcta.

### Caso 4 · `GET /api/agents` (Q5)

**Problema.** El controller pedía **el millón de tickets** al repositorio y los agrupaba en memoria (LINQ to Objects): 17,823 lecturas, más un millón de objetos viajando de la base a la API para devolver 8 filas.

| Paso | Cambio | Lecturas | Memoria concedida | CPU |
|---|---|---:|---:|---:|
| 0 | Agrupar en C# | 17,823 (+ 1 M de filas transferidas) | — | — |
| 1 | `GroupBy` en SQL con EF Core (`GROUP BY` + `COUNT(CASE ...)`) | 11,689 | **320 MB** | 774 ms |
| 2 | `INCLUDE (Status)` en el índice existente `IX_Tickets_AssignedTo` | **5,002** | **7 MB** | 455 ms |

**Paso 1, plan.** SQL Server cruzaba dos índices (`IX_Tickets_AssignedTo` + `IX_Tickets_Status_Priority`) con un *Hash Match* de 1 M de filas, porque ninguno tenía las dos columnas.
**Paso 2, plan.** Un solo índice, ya ordenado por agente, con *Stream Aggregate*: sin hash y casi sin memoria.

**Lección.** Antes de crear un índice nuevo, conviene **ampliar uno existente** (`INCLUDE`): cada índice extra ocupa espacio y vuelve más lentos los `INSERT` y `UPDATE`. Contar 990 mil filas sigue costando 5 mil lecturas; si hiciera falta bajar más, el siguiente paso serían contadores precalculados o una vista indexada.
