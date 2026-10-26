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
| Paralelismo | Casos 1 a 7: valores de fábrica (`cost threshold for parallelism` 5, `MAXDOP` 0 = 12 hilos). Desde el caso 8: **50 y 8** |
| Aislamiento | **RCSI desde el primer día**: EF Core lo activa al crear la base (`IF SERVERPROPERTY('EngineEdition') <> 5 ... SET READ_COMMITTED_SNAPSHOT ON`, en `Microsoft.EntityFrameworkCore.SqlServer`). Desde el caso 11: ADR y *optimized locking*, con la migración `EnableOptimizedLocking` |
| Herramientas | First Responder Kit **20260708**, instalado en `master` ([`fase4/11-first-responder-kit.sql`](fase4/11-first-responder-kit.sql)) · BenchmarkDotNet 0.15.8 ([`benchmarks/`](../benchmarks/)) |
| App | Desde el caso 15: aviso de consultas lentas (`SlowQueries:ThresholdMilliseconds`, 500 ms por defecto). Desde el caso 16: pool de `DbContext` |
| PostgreSQL | 18.6 en Docker, base `tuning` con los mismos datos ([`fase4/postgres/`](fase4/postgres/)). `pg_stat_statements` y `track_io_timing` activados en `deploy/compose.yaml` |
| Query Store | Encendido desde el caso 6 (`QUERY_CAPTURE_MODE = ALL`). Los arreglos de los casos 6 y 7 viven ahí, no en el código: [`fase4/06-query-store.sql`](fase4/06-query-store.sql) los documenta y los vuelve a aplicar |

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
2. **Q1: la estimación falla.** Estimó 10,037 filas (las de `Status = 'Open'`, como si el filtro por prioridad no redujera nada) y fueron 1,996. Con la estimación correcta, buscar en el índice habría ganado. *→ Casos 5 y 7.*
3. **Q2b reutiliza el plan cacheado de Q2a.** El plan se compiló para `ana` (10 mil filas) y se reutilizó para `pedro` (300 mil). Aquí da igual, porque los dos recorren la tabla, pero es la semilla del **parameter sniffing**. *→ Caso 6.*
4. **Q3 es la peor**, y es la consulta "optimizada" del N+1 de la Fase 3: `ROW_NUMBER() OVER (PARTITION BY TicketId ...)` sobre **todos** los comentarios obliga a ordenar 2.5 millones de filas para quedarse con 100 mil. Pide 590 MB de memoria: con 4 GB para toda la instancia, **6 ejecuciones simultáneas la agotarían**.
5. **Todo va en paralelo (DOP 12)**, incluso Q1 con un costo de 13.4. El umbral por defecto (`cost threshold for parallelism = 5`) viene de los años 90, y por eso una consulta de 2 mil filas gasta 282 ms de CPU repartidos en 12 hilos. *→ Caso 8.*
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

**Pendiente.** El *row goal* supone que las filas buscadas están repartidas de forma pareja. Para un valor **raro o inexistente** (un agente sin tickets) recorrería la tabla entera buscando 51 filas. *→ Caso 6.*

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

---

Los casos 5 a 8 no cambian índices ni consultas: corrigen **cómo decide el optimizador**. Scripts [`fase4/04`](fase4/04-estadisticas.sql) a [`fase4/07`](fase4/07-paralelismo.sql).

### Caso 5 · Q1: por qué estimó 10,037 filas (estadísticas)

**Problema.** Q1 (`Status = 'Open' AND Priority = 'High'`) estimó 10,037 filas y devolvió 1,996. Con cinco veces más filas de las reales, recorrer la tabla le pareció más barato que buscar en el índice.

**Qué sabe el optimizador** ([`fase4/04-estadisticas.sql`](fase4/04-estadisticas.sql)):

| Estadística | Qué dice |
|---|---|
| Histograma de `IX_Tickets_Status_Priority` | Solo de la **primera** columna: `Open` = 10,037 filas exactas (hay un escalón por valor, porque solo existen 4 estados) |
| Densidad de `(Status, Priority)` | 0.0625 = 1/16 combinaciones **en promedio**. No dice qué prioridades son más comunes dentro de cada estado |
| `_WA_Sys_..._Priority` | Estadística que SQL Server creó solo al ver `Priority` en un `WHERE`, con una **muestra** del 9.5 % de las filas |

**Lo que estimó cada estimador** (la versión "clásica" es la de SQL Server 7 a 2012; la nueva llegó en 2014 y es la de fábrica, nivel de compatibilidad 170):

| Estimador | Estimó | Cómo llegó | Plan de la página | Lecturas |
|---|---:|---|---|---:|
| Nuevo (170) | 10,037 | Usó la combinación de columnas del índice (`CSelCalcPointPredsFreqBased`, visible con el trace flag 2363) y le salió lo mismo que `Status = 'Open'` solo: como si `Priority` no filtrara nada | Clustered Index Scan | 449 |
| Clásico (70) | 2,509 | 10,037 × (0.0625 / 0.25): los `Open` repartidos parejo entre 4 prioridades | Index Seek + Key Lookup | **172** |
| *Real* | *1,996* | | | |

Sin paginar (la consulta de la Fase 3) la diferencia es mayor: 17,569 → 6,140 lecturas.

**Lecciones.**
- Una estadística de varias columnas guarda el histograma de la primera y solo **promedios** de las demás. Cuando los datos están sesgados (20 % de prioridad alta, no 25 %), el estimador adivina.
- Ningún estimador es "el bueno". El nuevo acierta más en general (por eso es el de fábrica), pero en esta consulta el clásico se acercó más. Se decide midiendo, consulta por consulta, y sin cambiar el de toda la base.
- `USE HINT('ASSUME_FULL_INDEPENDENCE')` no movió la estimación; cambiar de estimador sí.
- El SQL lo genera EF Core, así que no podemos agregarle el hint en el texto: lo aplica Query Store en el caso 7.

### Caso 6 · Parameter sniffing en la página por agente

**Problema.** (El pendiente del caso 1.) `GET /api/tickets?assignedTo=X` usa **un solo plan** para todos los agentes: el que se compiló con el primer valor que llegó. [`fase4/05-parameter-sniffing.sql`](fase4/05-parameter-sniffing.sql) vacía el caché y prueba los dos órdenes:

| Plan compilado con | Plan | pedro (30 % de los tickets) | nadie (0 tickets) |
|---|---|---:|---:|
| pedro | Clustered Index Scan en orden de Id, con *row goal* | **19** | **17,569** (toda la tabla) |
| nadie | Index Seek en `IX_Tickets_AssignedTo` + Key Lookup | 157 | **4** |

**Por qué.** Con pedro, el optimizador calcula que las 51 filas aparecen en las primeras páginas (*row goal*: casi una de cada tres es suya) y recorre la tabla en orden de Id. Funciona hasta que el valor es raro o no existe: entonces lee el millón de filas buscando 51 que no están. El seek, en cambio, ya entrega las filas en orden de Id (la clave del índice es `AssignedTo` + `Id`), así que hace como mucho 51 *lookups*.

El sniffing no es un error: es una **apuesta** que sale bien o mal según el primer valor. Ningún plan es el mejor para todos; la pregunta útil es cuál tiene un **peor caso aceptable**. El scan va de 19 a 17,569 lecturas; el seek, de 4 a unas 190.

**Opciones** (medidas con pedro / nadie):

| Opción | pedro | nadie | Costo |
|---|---:|---:|---|
| `OPTION (RECOMPILE)` | 19 | 4 | Compila en **cada** llamada (CPU), y hay que agregarlo al SQL |
| `OPTIMIZE FOR UNKNOWN` | 19 | 17,569 | Compila para el agente "promedio" (~111 mil tickets) y elige el scan: estable, pero malo para los raros |
| PSP (*Parameter Sensitive Plan*, SQL Server 2022+) | — | — | Debería guardar varios planes según el valor, pero no se activó. El evento extendido `parameter_sensitive_plan_optimization_skipped_reason` dijo `SkewnessThresholdNotMet`: 30 % contra 1 % no le parece suficiente sesgo |
| **Forzar el plan del seek con Query Store** ✔ | 169 | 4 | Nada en el código. Hay que vigilar que el plan siga siendo válido |

**Resultado desde la API**, con el plan forzado por [`fase4/06-query-store.sql`](fase4/06-query-store.sql):

| | Lecturas promedio | Máximo |
|---|---:|---:|
| Antes (plan del scan) | 13,181 | 17,569 |
| Después (plan forzado) | **67** | 193 |

Al forzar un plan, SQL Server lo vuelve a compilar con la misma forma, y Query Store puede guardarlo con otro `plan_id`: las ejecuciones "después" aparecen en un plan nuevo, no en el que se forzó.

### Caso 7 · Q1 sin tocar el código: un Query Store hint

**Problema.** El caso 5 mostró que Q1 elige mejor plan con el estimador clásico, pero el SQL lo genera EF Core. Agregarle `OPTION (...)` exigiría un interceptor o escribir el SQL a mano.

**Cambio.** `sys.sp_query_store_set_hints` (SQL Server 2022+) asocia el hint a la consulta guardada en Query Store, y SQL Server lo agrega en cada compilación, como si viniera en el texto. Lo aplica [`fase4/06-query-store.sql`](fase4/06-query-store.sql).

| | Plan | Lecturas por página (desde la API) |
|---|---|---:|
| Antes | Clustered Index Scan con *row goal* | 449 |
| Después: `USE HINT('FORCE_LEGACY_CARDINALITY_ESTIMATION')` | Index Seek + Key Lookup | **178** |

**Forzar un plan o poner un hint:**

| | Plan forzado (caso 6) | Query Store hint (caso 7) |
|---|---|---|
| Qué fija | Un plan exacto, que ya se usó alguna vez | Una instrucción; el optimizador sigue calculando el plan |
| Si cambian los índices o los datos | Puede dejar de ser válido: SQL Server lo ignora y lo anota en `force_failure_count` | Se adapta |
| Cuándo conviene | Conoces el plan bueno y lo quieres estable | Sabes qué corrige el problema (estimador, `MAXDOP`, `RECOMPILE`...) |

**Lo que hay que saber de Query Store** (todo esto pasó en este caso):
- **`query_id` es un texto exacto.** El mismo SQL desde la app (saltos de línea CRLF) y desde un script (LF) son dos `query_id`. El primer plan forzado quedó en la consulta del script, y la app siguió igual hasta repetirlo en la suya.
- **`query_hash` agrupa lógica parecida**, incluso `TOP (@p)` con `OFFSET ... FETCH` de la misma consulta. Sirve para sumar consumo, no para decidir a qué consulta aplicarle un arreglo.
- **Query Store descarta el comentario de `TagWith`**, porque EF Core lo pone antes del `SELECT`. Para encontrar una consulta, busca por su texto o pon la etiqueta dentro de la consulta.
- **Solo se puede forzar un plan que Query Store ya vio** para ese `query_id`.
- Los arreglos viven **en la base, no en el repositorio**. Si se restaura un respaldo viejo o se crea la base desde las migraciones, desaparecen: por eso `06-query-store.sql` los deja escritos y los vuelve a aplicar.

### Caso 8 · Paralelismo: `cost threshold for parallelism` y `MAXDOP` (Q5)

**Problema.** Con los valores de fábrica, cualquier plan que cueste más de 5 puede usar los 12 CPUs. Q5 (`GET /api/agents`) cuesta 5.24 en serie, así que corre en paralelo.

**Una sola sesión** ([`fase4/07-paralelismo.sql`](fase4/07-paralelismo.sql)):

| MAXDOP | CPU | Tiempo de respuesta | Costo estimado |
|---:|---:|---:|---:|
| 1 | 205 ms | 205 ms | 5.24 |
| 2 | 249 ms | 125 ms | 4.43 |
| 4 | 243 ms | 63 ms | 3.99 |
| 8 | 351 ms | 47 ms | 3.84 |
| 12 | 461 ms | **42 ms** | 3.84 |

Sola, la versión paralela responde 5 veces más rápido a cambio de 2.2 veces más CPU. Los últimos 4 hilos (de 8 a 12) compraron 5 ms con 110 ms de CPU. Y el costo **baja** con más hilos, porque el optimizador reparte la CPU estimada entre ellos: una vez que considera el paralelismo, el plan paralelo casi siempre gana en el papel.

**Con varios usuarios a la vez** ([`fase4/Medir-Concurrencia.ps1`](fase4/Medir-Concurrencia.ps1), 5 consultas por sesión):

| | 12 sesiones, DOP 12 | 12 sesiones, DOP 1 | 24 sesiones, DOP 12 | 24 sesiones, DOP 1 |
|---|---:|---:|---:|---:|
| CPU por consulta | 515 ms | 342 ms | 503 ms | 381 ms |
| Tiempo de respuesta promedio | 507 ms | 473 ms | 960 ms | 838 ms |
| Peor caso | 987 ms | 1,285 ms | 2,138 ms | 1,608 ms |
| Tiempo total de la prueba | 3.0–3.1 s | 3.4–4.5 s | 5.9 s | 6.2 s |

Con carga, la ventaja desaparece: la respuesta de la versión paralela pasa de 42 ms a medio segundo, igual que la serial, y cada consulta sigue gastando entre 30 y 50 % más CPU. Con 12 sesiones el lote paralelo terminó un poco antes (probablemente aprovecha los ratos en que algún CPU queda libre). Con 24, el doble de sesiones que CPUs, ya empata en total y su peor caso es peor.

**Cambio.** `cost threshold for parallelism` = **50** (punto de partida habitual; se ajusta mirando el costo de los planes reales) y `MAXDOP` = **8** (recomendación de Microsoft para un solo nodo NUMA con más de 8 CPUs lógicos). Q5 ahora corre en serie sin ningún hint: 205 ms de CPU y de respuesta.

**Lección.** El paralelismo cambia CPU por tiempo de respuesta, y solo conviene si **sobra** CPU. En una API, donde lo normal es que haya muchas peticiones a la vez, la CPU es el recurso compartido. Para una sola consulta sí perdimos: Q5 tarda 205 ms en vez de 42. El arreglo de fondo no es darle más hilos, sino no contar un millón de filas en cada llamada: guardar la respuesta en caché (Fase 7).

---

Los casos 9 a 13 son de **concurrencia**: qué pasa cuando varias personas usan la app a la vez. Scripts [`fase4/08`](fase4/08-quien-bloquea.sql) a [`fase4/11`](fase4/11-first-responder-kit.sql) y [`fase4/Demo-Bloqueos.ps1`](fase4/Demo-Bloqueos.ps1), que reproduce todo con el SQL de EF Core y termina en `ROLLBACK`.

### Caso 9 · La reasignación masiva bloquea la tabla entera

**Problema.** `POST /api/agents/pedro/reassign` mueve los 15,133 tickets abiertos de pedro a luis, en una transacción: lee los tickets, hace un `UPDATE` masivo y deja un comentario en cada uno. Medido con la app real (y los datos restaurados después):

| Mientras corre la reasignación | Tiempo |
|---|---:|
| La reasignación completa | **11.7 s**: 4.7 s el `UPDATE` (344,748 lecturas, unas 23 por ticket) y casi 7 s insertando 15,133 comentarios |
| `PUT` de un ticket de **luis**, que no tiene nada que ver con pedro | **10.2 s** esperando |
| `GET /api/dashboard` | 0.6 s: no espera |

**Por qué.** [`fase4/08-quien-bloquea.sql`](fase4/08-quien-bloquea.sql) durante la reasignación:
- La transacción no tiene 15 mil bloqueos de fila sino **uno solo: `OBJECT X` sobre `Tickets`**. Cuando una sentencia junta unos 5,000 bloqueos en una tabla, SQL Server los cambia por uno de toda la tabla (*lock escalation*) para ahorrar memoria. A partir de ahí, nadie más puede escribir en **ningún** ticket.
- Las lecturas pasan porque la base tiene RCSI (ver "Entorno"): leen la última versión confirmada, sin pedir bloqueos. Leer los tickets de pedro mientras se reasignan tardó 8 ms y devolvió los 15,133 de antes. Sin RCSI (`WITH (READCOMMITTEDLOCK)`), la misma lectura esperó 7.7 s.
- Comentar un ticket de luis también espera, aunque escribe en otra tabla: la llave foránea de `TicketComments` tiene que comprobar que el ticket existe, y eso lee `Tickets` con bloqueo.
- `BloqueadaPor` apunta a la sesión de **adelante en la cola**, no siempre al culpable: los bloqueos se atienden en orden de llegada. Por eso el script sube por la cadena hasta el **bloqueador raíz**.

| Operación, mientras la reasignación lleva su transacción abierta 6 s | Esperó |
|---|---:|
| Leer los tickets de pedro (RCSI, como la app) | 8 ms |
| Lo mismo sin RCSI (`READCOMMITTEDLOCK`) | 7,744 ms |
| Editar un ticket de luis | 7,846 ms |
| Comentar un ticket de luis | 7,805 ms |
| Crear un ticket nuevo | 8,035 ms |
| Editar un ticket de pedro (de los que se reasignan) | 7,769 ms |

Esperaron casi 8 s y no 6: el `ROLLBACK` del final tardó unos 2 s, porque deshace los 15 mil cambios uno por uno. **Deshacer no es gratis.**

### Caso 10 · Deadlock: dos reasignaciones en sentido contrario

**Problema.** Dos supervisores, al mismo tiempo: pedro → luis y luis → pedro. Con el `UPDATE` exacto de EF Core hubo deadlock en **6 de 8 intentos**. Desde la API, uno recibía **HTTP 500**.

**El grafo** ([`fase4/09-ultimo-deadlock.sql`](fase4/09-ultimo-deadlock.sql), que lo lee de la sesión `system_health`, siempre encendida):

| Recurso | Lo tenía | Lo esperaba |
|---|---|---|
| Página de `IX_Tickets_Open_AssignedTo` | pedro → luis (`U`) | luis → pedro (`IX`) |
| Otra página del mismo índice | luis → pedro (`U`) | pedro → luis (`IX`) |

El punto de encuentro es el índice filtrado del caso 2. Cada `UPDATE` lo recorre para encontrar sus tickets, y como cambia `AssignedTo`, que es la clave del índice, también tiene que mover cada entrada dentro de ese mismo índice: los de pedro van al rango de luis y los de luis al de pedro. Los dos caminos se cruzan. SQL Server rompe el ciclo cancelando a la transacción que menos log escribió (6.9 MB contra 8.0 MB), que es la más barata de deshacer.

Dos detalles para leer grafos:
- Los bloqueos de página no traen el nombre del índice, pero sí `associatedObjectId`, que `sys.partitions` traduce. Traducir el número de página no sirve: después del deadlock la página puede pertenecer a otra cosa.
- [`sp_BlitzLock`](fase4/11-first-responder-kit.sql) resumió el día igual: 7 deadlocks, todos en `Tickets`, todos con bloqueos de página, 1 min 40 s de espera en total.

### Caso 11 · *Optimized locking* (SQL Server 2025)

**Cambio.** `ALTER DATABASE ... SET OPTIMIZED_LOCKING = ON`, que necesita ADR (*Accelerated Database Recovery*) encendido antes. Funciona con dos piezas:
- **TID locking.** Cada fila modificada queda marcada con el número de la transacción. En vez de guardar un bloqueo por fila hasta el `COMMIT`, la transacción guarda **un solo bloqueo `XACT`**, y suelta el de cada fila apenas la modifica. Quien necesite una de esas filas espera a ese `XACT` (`LCK_M_S_XACT_*`). Con tan pocos bloqueos, no hay escalamiento.
- **LAQ (*lock after qualification*).** Requiere RCSI. El `WHERE` se evalúa sobre la última versión confirmada, sin bloquear, y solo se bloquean las filas que cumplen. Por eso las reasignaciones opuestas ya no chocan: luis → pedro ni siquiera mira los tickets que pedro → luis está moviendo, porque en la versión confirmada todavía son de pedro.

| | Antes | Con *optimized locking* |
|---|---:|---:|
| Bloqueos de la reasignación | `OBJECT X` (toda la tabla) | `OBJECT IX` + un `XACT X` |
| Editar un ticket de luis | 7,846 ms | **0 ms** |
| Comentar un ticket de luis | 7,805 ms | **4 ms** |
| Crear un ticket nuevo | 8,035 ms | **112 ms** |
| Editar un ticket de pedro (de los que se reasignan) | 7,769 ms | 5,336 ms (correcto: esa fila sí cambió) |
| Lectura con bloqueo (`READCOMMITTEDLOCK`) | 7,744 ms | 5,467 ms (también lee filas de pedro) |
| Deadlock pedro ⇄ luis | 6 de 8 intentos | **0 de 7** |

Las esperas legítimas también bajaron de unos 7.8 s a unos 5.4 s: con ADR el `ROLLBACK` es instantáneo, porque no deshace fila por fila.

**Va en una migración** (`EnableOptimizedLocking`), igual que EF ya hace con RCSI: es configuración de la base que la app necesita, y si alguien recrea la base tiene que quedar igual. Usa SQL dinámico porque la opción no existe antes de SQL Server 2025, y solo cambia lo que falta, así que aplicarla dos veces no hace nada. Probado: encender y apagar *optimized locking* con otra transacción abierta no necesitó acceso exclusivo a la base. Para ADR no lo probé, porque ya estaba encendido.

⚠️ **Una medición rara.** La primera corrida justo después de encenderlo todavía escaló a `OBJECT X`. No lo pude reproducir: ni con un plan compilado antes de encenderlo, ni apagando y volviendo a encender ADR y *optimized locking*. Todas las corridas siguientes, más de diez entre demos y pruebas sueltas, se comportaron como en la tabla. Queda anotado, y es otra razón para medir siempre más de una vez.

### Caso 12 · La app ante un deadlock: reintentar y responder 409

*Optimized locking* quitó **este** deadlock, pero puede haber otros. La app tiene que saber qué hacer cuando le toca ser la víctima. Prueba con dos reasignaciones opuestas por la API; para provocar el deadlock, se apagó *optimized locking* un momento:

| Código de la app | pedro → luis | luis → pedro |
|---|---|---|
| Sin reintentos (antes) | 200 | **500**: error 1205, víctima del deadlock |
| `EnableRetryOnFailure` | 200 | Reintentada sola; al repetirse encontró los tickets de pedro mezclados con los de luis, el conteo no cuadró y respondió **409** ("alguien más modificó este registro") |
| Con *optimized locking* (la migración) | 200 | **200**, al mismo tiempo y sin reintentos (9,947 tickets) |

**Cambios en el código:**
- `UseSqlServer(..., sqlServer => sqlServer.EnableRetryOnFailure())` reintenta los errores transitorios: deadlock 1205, cortes de conexión, *failover*. Hasta 6 veces, con esperas crecientes.
- Con reintentos activos, EF **no permite** abrir una transacción propia fuera de la estrategia, porque hay que repetir **toda** la transacción, no solo la última sentencia. `ReassignTicketsHandler` ahora corre dentro de `CreateExecutionStrategy().ExecuteAsync(...)`, y cada intento empieza con `ChangeTracker.Clear()`, para no arrastrar los comentarios del intento fallido.
- El 409 lo da la verificación que el handler ya tenía (lo leído contra lo actualizado). No es un error transitorio, así que no se reintenta.

Tres capas: *optimized locking* evita la causa, el reintento cubre los deadlocks que queden y la verificación protege los datos.

### Caso 13 · Diagnóstico: estadísticas de espera y First Responder Kit

**Estadísticas de espera** ([`fase4/10-esperas.sql`](fase4/10-esperas.sql): dos fotos de `sys.dm_os_wait_stats` con 20 s de diferencia, sin las esperas de fondo). Lo acumulado desde el arranque sirve poco; lo que dice algo es la diferencia mientras pasa el problema:

| Carga durante la medición | Esperas principales | Qué dice |
|---|---|---|
| 12 sesiones con Q5 en paralelo (caso 8) | `SOS_SCHEDULER_YIELD` 71 %, `LATCH_EX` 12 %, `CX*` 13 %, **`THREADPOOL` 80 veces (2.3 s)** | CPU saturada y hilos esperando a hilos. `THREADPOOL`: peticiones que esperaron un hilo libre solo para empezar |
| Reasignación, sin *optimized locking* | `LCK_M_IX` + `LCK_M_IS`: 27.2 s, 5 sesiones | Bloqueos sobre la **tabla** |
| Reasignación, con *optimized locking* | `LCK_M_S_XACT_READ` + `LCK_M_S_XACT_MODIFY`: 10.2 s, 2 sesiones | Bloqueos sobre la **transacción**, solo de quien toca tickets de pedro |

Una vez apareció `SEQUENCE_GENERATION` (2.8 s) al crear un ticket: la secuencia `TicketIds` guarda 50 valores en memoria, y al agotarlos escribe el nuevo tope en disco (aquí, un HDD). No lo investigué más.

**First Responder Kit** ([`fase4/11-first-responder-kit.sql`](fase4/11-first-responder-kit.sql)):

| Herramienta | Hallazgo | Veredicto |
|---|---|---|
| `sp_Blitz` | Sin respaldos ni `CHECKDB`, Agent apagado, sin alertas | Normal en desarrollo; en producción, lo primero que hay que resolver |
| `sp_Blitz` | *Slow Storage Writes* (más de 100 ms de promedio), *TempDB Only Has 1 Data File*, *Max Memory Set Too High* | Reales: el HDD y la configuración de fábrica del contenedor. `MSSQL_MEMORY_LIMIT_MB` ya limita la memoria a 4 GB; en un servidor normal, fijar `max server memory` es de lo primero |
| `sp_Blitz` | `cost threshold` 50, `MAXDOP` 8, `QUERY_CAPTURE_MODE = ALL`, RCSI, ADR, `DBCC FREEPROCCACHE` | Son cambios nuestros. Bien que los vea: así se nota cuando alguien tocó algo. En producción, Query Store con `AUTO` |
| `sp_BlitzIndex` | "Duplicados": `IX_Tickets_AssignedTo` e `IX_Tickets_Open_AssignedTo` | **No lo son**: misma clave, pero uno es filtrado (solo abiertos, el dashboard) y el otro completo (página por agente, `GET /api/agents`). Se quedan los dos |
| `sp_BlitzIndex` | Índice faltante `(AssignedTo, Status) INCLUDE (Title, Priority, CreatedAt, UpdatedAt, Version)`, impacto 89 % | **Rechazado**: sería una segunda copia de la tabla. Lo pide el `SELECT` de la reasignación (29,285 lecturas, recorre la tabla), que lee los tickets completos aunque solo necesita su Id. El arreglo es leer menos: Bloque 5 |
| `sp_BlitzIndex` | *Cascading Updates or Deletes* en `TicketComments` | A propósito: borrar un ticket borra sus comentarios |
| `sp_BlitzCache` | *Long Running With Low CPU* en la lectura con `READCOMMITTEDLOCK` | Una consulta que tarda pero casi no trabaja: está esperando, no calculando |
| `sp_BlitzLock` | 7 deadlocks, todos en `Tickets`, con bloqueos de página; 2 de la app | Coincide con el caso 10 |

**Lección.** Las herramientas encuentran rápido, pero **no deciden**: de los tres hallazgos sobre índices, uno era falso, otro se rechaza y el tercero es intencional. Cada sugerencia se evalúa contra lo que la app de verdad hace.

**Pendiente para el Bloque 5 (desde la aplicación).** La reasignación lee los 15 mil tickets completos solo para crear sus comentarios, e inserta esos comentarios en lotes de EF (unos 7 s de los 11.7). Mientras tanto, los tickets de pedro siguen bloqueados. *→ Caso 14.*

---

Los casos 14 a 16 miran **desde la aplicación**; el 17 y el 18 repiten lo aprendido en **PostgreSQL**.

### Caso 14 · La reasignación: de 362 sentencias a 2

**Problema** (el pendiente del caso 13). El handler leía los 15,133 tickets completos, los convertía en objetos del dominio solo para usar `ticket.CreateComment(...)` y guardaba los comentarios con EF, que los manda en lotes.

**Cambio.** Dos sentencias masivas dentro de la misma transacción:
1. `INSERT INTO TicketComments ... SELECT [t].[Id], ... FROM Tickets WHERE <agente y abiertos>` (en `ICommentRepository`). EF Core no tiene `INSERT ... SELECT` en LINQ, así que es SQL a mano con `ExecuteSqlAsync`, que convierte cada `{valor}` en un parámetro.
2. El mismo `ExecuteUpdate` de antes.

Las dos eligen los tickets con el mismo `WHERE`. Si los números no cuadran, alguien cambió un ticket entre una y otra: se responde 409, como antes. La regla del dominio (un ticket cerrado no admite comentarios) la cumple ese `WHERE`, que solo toma abiertos. Es el mismo criterio que ya tenía el `UPDATE`: las operaciones masivas no pasan por los objetos del dominio.

**Medición.** El tiempo de reloj en esta máquina no sirvió: la misma versión vieja tardó 12.1, 7.8 y 3.9 s en tres corridas (HDD, caché, estadísticas recién recalculadas). Lo confiable es lo que registra Query Store de cada sentencia:

| Paso (por reasignación) | Antes | Después |
|---|---|---|
| Leer los tickets | 1 `SELECT` con todas las columnas: 264 ms, 30,941 lecturas, recorre la tabla | — |
| Crear los comentarios | **361 lotes** de EF: unos 2.9 s, unas 150 mil lecturas | **1** `INSERT ... SELECT`: 1.6 s, 109 mil lecturas |
| Mover los tickets | `UPDATE`: 2.8 s, 369 mil lecturas | igual |
| Viajes a la base | 363 | 2 |

Además, los tickets de pedro quedan bloqueados solo mientras corre el `UPDATE`, que ahora es lo último antes del `COMMIT`. Antes también lo estaban durante los lotes de comentarios: un `PUT` sobre uno de ellos esperó entre 7.4 y 11.8 s.

**Lo que queda caro es el `UPDATE`**, unas 24 lecturas por ticket. El plan lo explica: actualiza 15,133 filas en la tabla y luego, en **cada uno de los dos índices cuya clave es `AssignedTo`**, convierte cada cambio en un borrado más una inserción (30,266 operaciones por índice). Es el costo de escritura de tener los dos índices que `sp_BlitzIndex` llamó "duplicados" (caso 13): se pagan en cada reasignación a cambio de lecturas rápidas en el dashboard y en la página por agente.

### Caso 15 · Encontrar las consultas lentas desde la app

**Problema.** El log de EF Core muestra **todas** las consultas, como `Information`. En producción eso se apaga, y entonces no queda nada.

**Cambio.** `SlowQueryInterceptor`, un `DbCommandInterceptor` que registra como **`Warning`**, con el SQL completo, solo las sentencias que pasan el umbral `SlowQueries:ThresholdMilliseconds` (500 ms; se lee en cada consulta, así que se puede cambiar sin reiniciar). El mensaje se escribe con `[LoggerMessage]`, que genera el código del log al compilar. También se agregaron etiquetas `TagWith` a las consultas del repositorio, y un comentario equivalente al SQL a mano: así cada aviso dice de qué parte del código salió.

Con el umbral en 100 ms, de seis llamadas a la API solo avisó `GET /api/agents` (Q5, unos 230 ms), con su etiqueta "Resumen de carga por agente". Durante una reasignación:

| Aviso | Qué enseñó |
|---|---|
| `INSERT ... SELECT` (705 ms), con su comentario | La etiqueta funciona también en SQL a mano |
| `UPDATE` de un ticket (542 ms) | **Una consulta lenta puede ser una consulta bloqueada**: el tiempo incluye lo que esperó (aquí, a que el `INSERT` terminara de revisar la llave foránea) |
| `UPDATE` masivo (2.6 s), **sin etiqueta** | **`ExecuteUpdate` ignora `TagWith`** (EF Core 10). Se quitó del código para que no prometa algo que no hace |

**Dos comprobaciones:**
- **Agregar `TagWith` no cambió el `query_id` en Query Store.** Las páginas de tickets siguieron cayendo en las consultas 93 y 96, con su plan forzado y su hint (casos 6 y 7): Query Store guarda la sentencia sin el comentario de adelante.
- **Dapper no pasa por el interceptor**: usa la conexión de EF, pero crea sus propios comandos. Para cubrir todo hace falta trazar a nivel de conexión (OpenTelemetry, Fase 11).

### Caso 16 · Consultas compiladas y pool de `DbContext` (BenchmarkDotNet)

**Pregunta.** Las dos optimizaciones de EF Core que más se recomiendan, ¿cuánto valen en la consulta más frecuente, `GET /api/tickets/{id}`? Medido con BenchmarkDotNet ([`benchmarks/`](../benchmarks/)) contra la base real, con un `DbContext` por operación, como cada petición HTTP:

| Variante | Tiempo | Memoria por operación |
|---|---:|---:|
| LINQ (como estaba) | 872 µs | 77.6 KB |
| Consulta compilada (`EF.CompileAsyncQuery`) | 822 µs (−6 %) | 71.2 KB |
| LINQ + pool de `DbContext` | 740 µs (−15 %) | **14.4 KB (−81 %)** |
| Compilada + pool | 703 µs (−19 %) | 9.1 KB (−88 %) |

**Decisión.** Se adoptó el **pool**: es una línea (`AddDbContextPool`) y cumple sus requisitos (el contexto no guarda estado propio y los interceptores son singleton). **La consulta compilada no**: exige un delegado estático por consulta, no se puede componer con otros filtros, y gana un 6 %. EF ya guarda la traducción del LINQ en su caché; lo que pesaba era construir un `DbContext` nuevo en cada petición. Casi todo el tiempo restante (unos 700 µs) es el viaje a SQL Server.

### Caso 17 · PostgreSQL: los mismos planes con `EXPLAIN (ANALYZE, BUFFERS)`

Los mismos datos (1 M de tickets, 2.5 M de comentarios, la misma distribución) en PostgreSQL 18 ([`fase4/postgres/`](fase4/postgres/)). `BUFFERS` cuenta páginas de 8 KB, como las lecturas lógicas: *shared hit* = en memoria, *read* = del disco. La tabla ocupa **119 MB contra 225 MB en SQL Server**: `nvarchar` guarda 2 bytes por carácter, PostgreSQL usa UTF-8, y RCSI agrega 14 bytes por fila.

| Consulta | PostgreSQL | SQL Server | Comentario |
|---|---:|---:|---|
| Q1 (Open + High), página de 51 | 465 páginas | 449 | **El mismo plan**: recorrer la tabla en orden de `id` hasta juntar 51. PostgreSQL estimó bien (1,839 a 2,191 filas, según la muestra de `ANALYZE`) y aun así eligió este plan |
| Q4 (dashboard) | **300 páginas**, Index Only Scan | 409 | El índice parcial es el filtrado. `Heap Fetches: 0` gracias a `VACUUM`: sin el mapa de visibilidad, igual tendría que ir a la tabla. SQL Server no lo necesita |
| Etiqueta rara, sin índice | 11,991 páginas, 93 ms | — | Recorre la tabla buscando 51 de 200 |
| Etiqueta rara, con **GIN** | **202 páginas, 0.9 ms** | — | Índice invertido: de cada etiqueta, a sus tickets |
| Etiqueta común, con GIN | 13 páginas | — | El planificador **no usa** el GIN: recorrer la tabla encuentra 51 enseguida |

**El parameter sniffing de PostgreSQL** (Q2, página por agente, como sentencia preparada):

| | Plan | pedro | agente sin tickets |
|---|---|---:|---:|
| A la medida (las primeras 5 veces) | El mejor para cada valor | 6 páginas | 6 páginas |
| Genérico (forzado) | El del agente "promedio" (123,721 tickets) | 6 páginas | **19,985 páginas, 211 ms** |
| **Modo por defecto (`auto`)** | Tras 8 ejecuciones alternando agentes: **0 genéricos, 8 a la medida** | | |

En su modo por defecto, PostgreSQL compara el costo del plan genérico con el de los planes a la medida. Si el genérico parece peor, sigue planificando cada vez (unos 0.1 ms): algo así como poner `OPTION (RECOMPILE)` solo cuando conviene. Forzado, el genérico se comporta igual que `OPTIMIZE FOR UNKNOWN` en el caso 6. Dos detalles más:
- En un plan genérico, **`LIMIT $2` es un parámetro y PostgreSQL supone el 10 % de las filas**. Es lo que genera EF Core con `Take(n)`.
- Probé un índice `(assigned_to, id)` creyendo que arreglaría el plan genérico, y no: para el agente "promedio", recorrer la tabla en orden sigue siendo lo más barato. Se quitó del script.

**`pg_stat_statements`** es el equivalente de Query Store: agrupa las consultas normalizadas (las constantes se vuelven `$1`), con llamadas, tiempo y páginas.

### Caso 18 · PostgreSQL: versiones muertas, HOT y `VACUUM`

En PostgreSQL un `UPDATE` no sobrescribe la fila: escribe una versión nueva y deja la vieja "muerta" ([`fase4/postgres/pg-03-vacuum.sql`](fase4/postgres/pg-03-vacuum.sql)). Con la reasignación de pedro (15,040 tickets en estos datos):

| Paso | Resultado |
|---|---|
| `UPDATE` + `ROLLBACK` | El `ROLLBACK` tardó **0.4 ms**: no hay nada que deshacer, las versiones nuevas quedan muertas. Es lo que ADR consigue en SQL Server (caso 11) |
| Después | **15,040 versiones muertas** en la tabla, ocupando lugar hasta que alguien las limpie |
| `VACUUM` | 0.25 s, 29,968 versiones quitadas de la tabla y de sus 5 índices |
| ¿Cuándo actúa solo autovacuum? | Con más de **200,050** muertas en esta tabla (50 + 20 % de las filas): 15 mil no lo despiertan |

**HOT (*heap-only tuple*).** Si el `UPDATE` no cambia ninguna columna indexada **y** la versión nueva cabe en la misma página, PostgreSQL no toca los índices. Si no, agrega una entrada nueva en **todos** los índices, aunque sus columnas no hayan cambiado.

| `UPDATE` de `updated_at` (sin índice) en 15,040 filas | HOT | Tiempo | Tamaño de la tabla |
|---|---:|---:|---:|
| Páginas llenas (`fillfactor` 100, como queda tras la carga) | **77** | 237 a 456 ms | 119 MB |
| `fillfactor` 90 (10 % libre en cada página) | **15,038** | 124 ms | 133 MB |

**La comparación de fondo.** SQL Server y PostgreSQL resuelven "leer sin bloquear" de forma parecida: guardan versiones de las filas (RCSI, ADR / MVCC). La diferencia está en quién limpia. En SQL Server, el version store de tempdb o el de ADR se limpian solos. En PostgreSQL las versiones viejas viven en la misma tabla, y `VACUUM` (o autovacuum) es parte del mantenimiento: si se atrasa, la tabla y sus índices crecen.

---

## Trampas de herramientas

| Trampa | Síntoma | Solución |
|---|---|---|
| `sqlcmd` con `QUOTED_IDENTIFIER OFF` | No usa índices filtrados, y no permite `INSERT`/`UPDATE` en tablas que los tienen | `sqlcmd -I` (caso 2) |
| Un comentario `/* */` dentro de otro, seguido de un `?` en el script | `sqlcmd` (ODBC) **no ejecuta el lote ni muestra error**, ni siquiera con un `SELECT 1/0`. Probablemente el controlador cree que el comentario terminó en el primer `*/` y toma el `?` por un parámetro | No anidar comentarios de bloque |
| `RECONFIGURE` de opciones como `MAXDOP` o `cost threshold` | Vacía el caché de planes de **toda la instancia**, con sus métricas (`sys.dm_exec_query_stats`) | Leer las métricas antes, y en producción cambiarlas en horario tranquilo |
| `INSERT ... EXEC` para descartar filas | Las lecturas de `sys.dm_exec_query_stats` suman unas 2 por fila, que son las escrituras en la tabla destino | Tomar las lecturas de `STATISTICS IO`, línea por tabla (script 04) |
| Encender Query Store y medir en seguida | La primera corrida puede no quedar registrada (encenderlo es asíncrono) | Esperar a que `actual_state_desc` sea `READ_WRITE` (script 05) |
| Métodos de XML (`.value()`, `.nodes()`) desde `sqlcmd` sin `-I` | Error 1934: `QUOTED_IDENTIFIER` incorrecto | `sqlcmd -I`, también para leer grafos de deadlock |
| El archivo `.xel` de `system_health` | Un deadlock recién ocurrido todavía no aparece | Leer primero el *ring buffer* de la sesión (script 09) |
| `NOT LIKE '%UPDATE%'` para descartar sentencias en Query Store | También descarta las que tienen la columna `UpdatedAt`, porque la intercalación no distingue mayúsculas | Buscar el texto exacto: `'%UPDATE \[t\]%' ESCAPE '\'` |
| Parámetros de EF Core en Query Store | Una variable capturada por una lambda puede llegar como `@_8__locals1_fromAgent` en vez de `@fromAgent` | Buscar por el resto del texto, no por el nombre del parámetro |
| `INSERT ... EXEC sp_BlitzIndex` | Falla: el procedimiento ya usa `INSERT ... EXEC` por dentro, y no se pueden anidar | Ejecutarlo directo |
| `sp_BlitzCache` después de un `RECONFIGURE` | Casi no ve consultas: el caché de planes se vació | La historia está en Query Store |
| `Invoke-WebRequest -Body $json` en PowerShell 5.1 con acentos | HTTP 400: el texto no viaja como UTF-8 y el JSON llega roto | Mandar bytes: `-Body ([Text.Encoding]::UTF8.GetBytes($json))` |
| Medir con el reloj en un disco HDD | La misma versión tardó 3.9, 7.8 y 12.1 s | Comparar lo que mide el servidor (Query Store: duración y lecturas por sentencia) |
| `TagWith` en `ExecuteUpdate` / `ExecuteDelete` (EF Core 10) | El comentario no llega al SQL | Identificar la sentencia por su texto |
| Sentencias preparadas en PostgreSQL con `LIMIT $n` | El plan genérico supone el 10 % de las filas | Saberlo al leer el plan; el modo `auto` suele evitarlo |
| `ANALYZE` de PostgreSQL | Las estimaciones cambian entre corridas: usa una muestra (30 mil filas por defecto) | Subir `default_statistics_target` en las columnas que lo necesiten |
