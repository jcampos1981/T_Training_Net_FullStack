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

## Trampas de herramientas

| Trampa | Síntoma | Solución |
|---|---|---|
| `sqlcmd` con `QUOTED_IDENTIFIER OFF` | No usa índices filtrados, y no permite `INSERT`/`UPDATE` en tablas que los tienen | `sqlcmd -I` (caso 2) |
| Un comentario `/* */` dentro de otro, seguido de un `?` en el script | `sqlcmd` (ODBC) **no ejecuta el lote ni muestra error**, ni siquiera con un `SELECT 1/0`. Probablemente el controlador cree que el comentario terminó en el primer `*/` y toma el `?` por un parámetro | No anidar comentarios de bloque |
| `RECONFIGURE` de opciones como `MAXDOP` o `cost threshold` | Vacía el caché de planes de **toda la instancia**, con sus métricas (`sys.dm_exec_query_stats`) | Leer las métricas antes, y en producción cambiarlas en horario tranquilo |
| `INSERT ... EXEC` para descartar filas | Las lecturas de `sys.dm_exec_query_stats` suman unas 2 por fila, que son las escrituras en la tabla destino | Tomar las lecturas de `STATISTICS IO`, línea por tabla (script 04) |
| Encender Query Store y medir en seguida | La primera corrida puede no quedar registrada (encenderlo es asíncrono) | Esperar a que `actual_state_desc` sea `READ_WRITE` (script 05) |
