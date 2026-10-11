# Plan de aprendizaje: Fullstack .NET 2026

> **Objetivo:** tener la base para trabajar en corporativo, startup o freelance con **.NET 10** (LTS, soporte hasta noviembre de 2028), **C# 14** y **EF Core 10**.
>
> **Ritmo supuesto:** unas 10 h por semana. Si tienes más o menos tiempo, escala las semanas; el orden se mantiene.
>
> **.NET 11** sale en noviembre de 2026 (STS, soporte corto). Revisa sus novedades, pero mantén el proyecto en .NET 10.

## Punto de partida

| Área | Situación actual | Enfoque del plan |
|---|---|---|
| C# | Sí, en WinForms / .NET Framework | Ponerse al día con C# moderno y la plataforma .NET actual |
| ASP.NET | Hasta ASP.NET 4.x | Reaprender: ASP.NET Core es otro framework |
| React + TypeScript | Dominado | No reaprender; aprender a integrarlo con .NET |
| Node.js | Uso diario | Usarlo como puente (ver tabla de equivalencias) |
| SQL | Dominado | Falta tuning y optimización: fase dedicada |
| Blazor | Sin experiencia | Segundo frontend |

## Proyecto ancla: HelpDesk

Una sola app que crece en cada fase. Un sistema de tickets de soporte obliga a tocar casi todo el stack:

- Usuarios con roles (cliente, agente, admin): autenticación y autorización
- Tickets, comentarios, categorías y adjuntos: relaciones en EF Core
- Dashboard con métricas (tiempos de respuesta, SLA, carga por agente): consultas pesadas para practicar tuning y Dapper
- Notificaciones en vivo: SignalR
- Revisión periódica de SLA: background services
- Respuestas sugeridas a partir de tickets parecidos: IA y búsqueda vectorial
- Portal del cliente en React y panel de admin en Blazor: ambos frontends
- Consola de escritorio para agentes: WPF
- Despliegue en un clúster: Kubernetes

Estructura sugerida:

```
/src
  HelpDesk.Api        ASP.NET Core
  HelpDesk.Admin      Blazor
  HelpDesk.Web        React + Vite
  HelpDesk.Desktop    WPF (pista de escritorio)
  HelpDesk.AppHost    Aspire (fase 11)
/tests
/deploy               docker compose, manifiestos de Kubernetes, Helm
/sql                  scripts, seeds y bitácora de tuning
/docs
```

## Equivalencias Node.js → .NET

| Node.js / TypeScript | .NET |
|---|---|
| npm, `package.json` | NuGet, `.csproj` con `PackageReference` |
| Rutas de Express / Fastify | Minimal APIs |
| Middleware de Express | Middleware de ASP.NET Core (`app.Use...`) |
| NestJS (controllers, DI, módulos) | Controllers + DI integrado |
| Prisma / TypeORM / Drizzle | EF Core |
| knex, consultas con `pg` | Dapper |
| zod / class-validator | Validación integrada, DataAnnotations, FluentValidation |
| Jest / Vitest | xUnit |
| supertest | `WebApplicationFactory` |
| dotenv, `process.env` | `IConfiguration`, `appsettings.json`, User Secrets |
| Socket.IO | SignalR |
| BullMQ, workers | `BackgroundService`, Hangfire, Quartz.NET |
| passport.js | Authentication handlers de ASP.NET Core |
| nodemon | `dotnet watch` |
| Promise + async/await | `Task` + async/await (sin event loop único: hay thread pool, así que bloquear con `.Result` sí cuesta) |

## ASP.NET 4.x → ASP.NET Core: qué cambió

| Antes (ASP.NET 4.x) | Ahora (ASP.NET Core) |
|---|---|
| `Global.asax`, `web.config` | `Program.cs`, `appsettings.json` |
| HttpModules y HttpHandlers | Middleware |
| `System.Web`, `HttpContext.Current` | `HttpContext` inyectado; no existe `System.Web` |
| Solo IIS y Windows | Kestrel multiplataforma (IIS opcional como proxy) |
| Unity / Autofac / Ninject | DI integrado |
| `packages.config` | `PackageReference` |
| MVC y Web API 2 separados | Unificados |
| Entity Framework 6 | EF Core (otra API y otro comportamiento) |

---

## Fases

### Fase 0: Entorno y Docker básico (2 semanas)

**Regla: todo lo nuevo va en H:** (C: tiene poco espacio). Carpeta base: `H:\DevTools`.

| Qué | Dónde | Cómo |
|---|---|---|
| WSL 2 (requisito de Docker Desktop) | Sin distro | `wsl --install --no-distribution`. Docker no necesita Ubuntu, y una distro se instalaría en C: |
| Docker Desktop (programa + datos de WSL, imágenes, contenedores y BD) | `H:\DevTools\Docker`, `H:\DevTools\docker-data` | `"Docker Desktop Installer.exe" install --installation-dir=H:\DevTools\Docker --wsl-default-data-root=H:\DevTools\docker-data`. Si ya está instalado: Settings → Resources → Advanced → Disk image location |
| Caché de NuGet (crece varios GB) | `H:\DevTools\nuget\packages` | Variable de entorno de usuario `NUGET_PACKAGES` |
| Caché de npm | `H:\DevTools\npm-cache` | `npm config set cache H:\DevTools\npm-cache` |
| Rider o VS 2026 (pista WPF) | `H:\DevTools\...` | Rider se instala completo en H:. VS 2026: instalador → "Ubicaciones de instalación" (una parte siempre queda en C:) |
| SDK de .NET 10 | `C:\Program Files\dotnet` | Excepción a la regla, ver decisiones |

**Decisiones tomadas (octubre 2026):**
- **SDK 10.0.1xx en C:.** El instalador oficial siempre usa C:. Para compensar, se desinstalaron .NET Core 2.1, 3.1 y 5.0 y el workload MAUI (≈15 GB liberados). Se eligió la banda **10.0.1xx**, no la 2xx/3xx/4xx, porque es la única que VS 2022 17.14 puede cargar. Al actualizar el SDK, hay que quedarse en 10.0.1xx mientras uses VS 2022.
- **IDE: VS Code + C# Dev Kit** para todo el plan web. Windows 10 no tiene soporte oficial para VS 2026, y VS 2022 no soporta proyectos `net10.0`.
- **VS 2022 (17.14)** se queda para los proyectos viejos de .NET Framework 4.x.
- **Pista WPF:** Rider, o VS 2026 si para entonces el equipo está en Windows 11.

- [x] Instalar el SDK de .NET 10 y verificar con `dotnet --info`
- [x] IDE: VS Code + C# Dev Kit
- [ ] Docker Desktop con los datos en H:
- [x] `NUGET_PACKAGES` apuntando a H:
- [x] Caché de npm apuntando a H:
- [ ] SQL Server y PostgreSQL en contenedores Docker, con named volumes (quedan dentro de los datos de Docker en H:)
- [x] `git init -b main` y `.gitignore` (`dotnet new gitignore`)
- [x] `global.json` fijando el SDK 10.0.112 (`dotnet new globaljson`)
- [x] Solución `HelpDesk.slnx` y carpetas `src`, `tests`, `deploy`, `sql`, `docs`
- [ ] Primer commit y repo en GitHub

**Docker básico.** Lo usarás desde ya para las bases de datos y después para Redis, Keycloak, RabbitMQ y Testcontainers.

- [ ] Imagen vs contenedor, capas y registros (Docker Hub, GHCR, ACR)
- [ ] `docker run`, `ps`, `logs`, `exec`, `stop`, `rm`; mapeo de puertos y variables de entorno
- [ ] Volúmenes (named volumes vs bind mounts) y redes
- [ ] Docker Compose: un `deploy/compose.yaml` que levante SQL Server, PostgreSQL y Redis
- [ ] Espacio en disco: `docker system df` y `docker system prune` (cada imagen de SQL Server pesa más de 1 GB)

**Entregable:** repo en GitHub con la solución compilando, y `docker compose up` levantando las bases de datos.

### Fase 1: C# moderno y plataforma .NET (2 semanas)

Objetivo: cerrar la brecha entre el C# de WinForms / .NET Framework y C# 14.

- [ ] Proyectos estilo SDK, CLI `dotnet`, `Directory.Build.props`
- [ ] Top-level statements, file-scoped namespaces, global usings
- [ ] Records, primary constructors, `required` / `init`
- [ ] Pattern matching: switch expressions, property patterns, list patterns
- [ ] Nullable reference types activado y sin warnings
- [ ] Collection expressions; novedades de C# 14 (extension members, palabra clave `field`)
- [ ] async/await a fondo: `Task` vs `ValueTask`, `CancellationToken`, por qué evitar `.Result` / `.Wait()`, `IAsyncEnumerable`
- [ ] LINQ a fondo, en especial la diferencia entre `IEnumerable` e `IQueryable` (clave para EF Core)
- [ ] Generic Host: DI, configuración, options pattern y logging

**Entregable:** app de consola con Generic Host que importa un CSV de tickets, lo valida y lo transforma, usando DI, `IOptions<T>` y logging.

**Ojo:** en WinForms casi nunca se usa DI; en .NET moderno todo se apoya en ella.

### Fase 2: ASP.NET Core Web API (3 semanas)

- [ ] Pipeline de middleware y su orden (compáralo con Express)
- [ ] Minimal APIs: endpoints, route groups, `TypedResults`
- [ ] Controllers y cuándo convienen
- [ ] Validación integrada de Minimal APIs (.NET 10) y FluentValidation
- [ ] Manejo de errores con `ProblemDetails` e `IExceptionHandler`
- [ ] OpenAPI integrado (`Microsoft.AspNetCore.OpenApi`) con UI de Scalar o Swagger UI
- [ ] Configuración por entorno y User Secrets
- [ ] Primeros tests unitarios con xUnit (desde aquí, cada fase agrega tests)

**Entregable:** API de tickets (CRUD) en memoria, documentada con OpenAPI.

### Fase 3: EF Core + Dapper (3 semanas)

- [ ] `DbContext`, configuración con Fluent API, migraciones
- [ ] Relaciones 1-N y N-N, owned types y complex types
- [ ] Tracking vs `AsNoTracking`, proyecciones con `Select`
- [ ] Problema N+1: `Include` vs proyección vs `AsSplitQuery`
- [ ] Ver el SQL generado: logging, `ToQueryString()`, `TagWith()`
- [ ] `ExecuteUpdate` / `ExecuteDelete` y transacciones
- [ ] Concurrencia optimista con `rowversion`
- [ ] Interceptores para auditoría (CreatedAt, UpdatedBy)
- [ ] Dapper para las consultas del dashboard
- [ ] Cambiar el proveedor a PostgreSQL (Npgsql) y ver qué se rompe

**Entregable:** API que persiste en SQL Server, con migraciones y un dashboard hecho con Dapper.

### Fase 4: SQL, tuning y optimización (4 semanas; puede ir en paralelo con las fases 5 a 7)

Tu brecha principal. Primero prepara datos realistas: genera millones de filas en HelpDesk con Bogus, o descarga la base de datos de Stack Overflow que publica Brent Ozar.

**SQL Server**

- [ ] Leer planes de ejecución (estimado vs real); operadores clave: seek, scan, key lookup, nested loops / hash / merge join, sort, spools
- [ ] `SET STATISTICS IO, TIME ON`: medir lecturas lógicas, no solo tiempo
- [ ] Índices: clustered vs nonclustered, columnas incluidas (covering), filtrados, orden de columnas
- [ ] Consultas SARGables: funciones sobre columnas, conversiones implícitas, `LIKE '%x'`
- [ ] Estadísticas y estimación de cardinalidad
- [ ] Parameter sniffing: síntomas y soluciones
- [ ] Query Store para encontrar regresiones
- [ ] DMVs y wait stats; First Responder Kit (`sp_Blitz`, `sp_BlitzIndex`, `sp_BlitzCache`)
- [ ] Bloqueos y deadlocks: niveles de aislamiento, RCSI, leer un deadlock graph
- [ ] Columnstore para consultas analíticas (conocer)

**PostgreSQL (lo básico)**

- [ ] `EXPLAIN (ANALYZE, BUFFERS)` y `pg_stat_statements`
- [ ] Índices B-tree, GIN y parciales; VACUUM y autovacuum

**Desde la aplicación**

- [ ] Detectar consultas lentas de EF Core con logs y `TagWith`
- [ ] Compiled queries; paginación por keyset vs `OFFSET`

**Entregable:** `sql/tuning-journal.md` con al menos 5 consultas lentas, cada una con plan antes y después, lecturas lógicas, tiempo y el cambio aplicado.

### Fase 5: Autenticación y autorización (2 semanas)

- [ ] ASP.NET Core Identity y `MapIdentityApi`
- [ ] JWT bearer: emisión, validación y refresh tokens
- [ ] Autorización por roles, claims y policies; autorización por recurso (un agente solo ve sus tickets)
- [ ] OpenID Connect con proveedor externo: Keycloak en Docker para practicar; conocer Entra ID (corporativo)
- [ ] SPA + API: CORS, y patrón BFF con cookies vs tokens en el navegador
- [ ] Rate limiting integrado

**Entregable:** login con roles y endpoints protegidos.

### Fase 6: Frontends, React + Blazor (4 semanas)

**React** (ya lo dominas; el foco es la integración)

- [ ] Cliente TypeScript generado desde OpenAPI (openapi-typescript, orval o Kiota)
- [ ] TanStack Query contra la API
- [ ] Flujo de autenticación del frontend con tu API
- [ ] Proxy de Vite en desarrollo; servir el build desde ASP.NET Core o por separado

**Blazor** (nuevo)

- [ ] Blazor Web App y modos de render (Static SSR, Interactive Server, WebAssembly, Auto) y cuándo usar cada uno
- [ ] Componentes, parámetros, ciclo de vida, `EventCallback`
- [ ] Formularios y validación con `EditForm`
- [ ] Autenticación en Blazor
- [ ] Una librería de componentes: MudBlazor, Fluent UI Blazor o Radzen

**Entregable:** portal del cliente en React y panel de admin en Blazor, ambos contra la misma API.

> **Hito 1 (alrededor de la semana 20):** fullstack .NET funcional. Con esto ya puedes postularte a vacantes.

### Fase 7: Tiempo real, background y caché (2 semanas)

- [ ] SignalR: notificaciones de tickets en React y en Blazor
- [ ] `BackgroundService` para revisar SLAs; conocer Hangfire o Quartz.NET para jobs programados
- [ ] HybridCache + Redis; output caching
- [ ] Health checks

**Entregable:** notificaciones en vivo y alertas de SLA.

> Buen momento para la **Pista de escritorio: WPF** (más abajo): ya tienes API, autenticación y SignalR para conectarla.

### Fase 8: Arquitectura y testing (3 semanas)

Va después de construir, a propósito: refactorizar algo que ya funciona enseña más que empezar con la arquitectura "perfecta".

- [ ] Clean Architecture vs Vertical Slice: refactoriza HelpDesk a una de las dos y documenta por qué la elegiste
- [ ] SOLID aplicado; DDD táctico básico (entidades, value objects, agregados)
- [ ] Result pattern vs excepciones
- [ ] Mediador y mapeo sin depender de librerías comerciales (MediatR y AutoMapper ya son de pago): implementación propia, mapeo manual o Mapperly
- [ ] Tests de integración con `WebApplicationFactory` + Testcontainers (SQL Server real)
- [ ] Mocks con NSubstitute o Moq
- [ ] Tests de arquitectura con NetArchTest o ArchUnitNET (opcional)

**Entregable:** proyecto refactorizado con la suite de tests en verde.

### Fase 9: Contenedores de la app, CI/CD y nube (3 semanas)

- [ ] Dockerfile multi-stage para la API y el panel Blazor; `.dockerignore`
- [ ] Alternativa sin Dockerfile: `dotnet publish /t:PublishContainer`
- [ ] Imágenes pequeñas y seguras: imágenes chiseled de .NET, usuario no root, puerto 8080 por defecto
- [ ] React: build con Node y servirlo con nginx o desde ASP.NET Core
- [ ] docker compose con la app completa: API, frontends, SQL Server y Redis
- [ ] Escanear vulnerabilidades de las imágenes (Docker Scout o Trivy)
- [ ] Publicar imágenes en un registro (GHCR o Azure Container Registry)
- [ ] GitHub Actions: build, tests, imagen y push
- [ ] Azure: App Service o Container Apps, Azure SQL, Key Vault, managed identity
- [ ] Conocer Azure DevOps Pipelines (común en corporativo)
- [ ] Conocer el equivalente en AWS (ECS, RDS)

**Entregable:** HelpDesk desplegado con URL pública y pipeline automático.

> **Hito 2 (alrededor de la semana 28):** app en producción con CI/CD.

### Fase 10: Kubernetes (3 semanas)

Nivel desarrollador: desplegar y operar tu app en un clúster, no administrar clústeres. Practica en local con el Kubernetes integrado de Docker Desktop (sus datos quedan en H:) o con kind.

- [ ] Arquitectura básica: clúster, nodos, control plane; `kubectl` y k9s
- [ ] Pods, Deployments y ReplicaSets; rolling updates y rollback
- [ ] Services (ClusterIP, NodePort, LoadBalancer) y namespaces
- [ ] Exponer la app hacia afuera con Ingress y Gateway API (el sucesor recomendado; el controlador ingress-nginx fue retirado)
- [ ] ConfigMaps y Secrets mapeados a `IConfiguration` (variables con `__` para claves anidadas)
- [ ] Probes de liveness, readiness y startup conectadas a los health checks de la Fase 7
- [ ] Requests y limits de CPU y memoria, y cómo los respeta el GC de .NET
- [ ] Apagado ordenado: SIGTERM, `IHostApplicationLifetime`, `terminationGracePeriodSeconds`
- [ ] Logs estructurados a stdout
- [ ] Autoescalado con HPA
- [ ] StatefulSets y volúmenes persistentes (para entenderlos; en producción conviene una base de datos administrada)
- [ ] Helm: instalar charts de terceros (Redis, RabbitMQ) y crear uno para HelpDesk; conocer Kustomize
- [ ] Conocer: AKS, GitOps (Argo CD o Flux), y que Azure Container Apps corre sobre Kubernetes sin que tengas que administrarlo

**Entregable:** HelpDesk corriendo en el clúster local con un chart de Helm, probes y autoescalado.

**Ojo con los costos:** si practicas en AKS, borra el clúster al terminar cada sesión.

**Certificación (opcional):** CKAD, la orientada a desarrolladores.

### Fase 11: Aspire, observabilidad y mensajería (3 semanas)

- [ ] Aspire: AppHost que orquesta API, React, Blazor, SQL Server y Redis
- [ ] Dashboard de Aspire y service defaults
- [ ] OpenTelemetry: logs estructurados, métricas y trazas distribuidas
- [ ] Mensajería con RabbitMQ (local) o Azure Service Bus: eventos "ticket creado" y "ticket cerrado"
- [ ] Patrón outbox, idempotencia y reintentos (`Microsoft.Extensions.Http.Resilience`)

**Entregable:** app orquestada con Aspire, con trazas de punta a punta y eventos por cola.

### Fase 12: IA aplicada (2 semanas)

- [ ] `Microsoft.Extensions.AI`: `IChatClient` y embeddings
- [ ] Búsqueda vectorial: pgvector en PostgreSQL o el tipo `vector` de SQL Server 2025 / Azure SQL
- [ ] RAG: sugerir respuestas a partir de tickets resueltos parecidos
- [ ] Tool calling; conocer Microsoft Agent Framework
- [ ] Servidor MCP en C# que exponga los tickets a un asistente (opcional)
- [ ] Evaluación y costos: tokens, latencia, respuestas incorrectas

**Entregable:** función "respuesta sugerida" en el panel del agente.

> **Hito 3 (alrededor de la semana 36):** perfil completo, con diferenciadores.

### Extra: modernización de legado (2 semanas, recomendado)

Tu experiencia con ASP.NET 4.x es una ventaja: muchas empresas tienen apps en .NET Framework que quieren migrar.

- [ ] Migración incremental (strangler fig) con YARP y System.Web adapters
- [ ] EF6 → EF Core: diferencias y estrategia
- [ ] Práctica: migrar una app tuya antigua o un ejemplo de ASP.NET MVC 5

### Opcionales según la vacante

- **Angular:** si un corporativo lo pide. Con TypeScript y React se aprende rápido.
- **gRPC**
- **Native AOT** con Minimal APIs
- **WinUI 3 o .NET MAUI:** si una vacante de escritorio o móvil lo pide; parten de lo aprendido en la pista WPF

---

## Pista de escritorio: WPF moderno (5–6 semanas)

**Cuándo:** después de la Fase 7, porque la app se conecta a la API, la autenticación y SignalR. Los bloques 1 a 4 (XAML y MVVM) solo requieren la Fase 1, así que puedes adelantarlos si te conviene.

**IDE:** Visual Studio o Rider (diseñador, XAML Hot Reload, árbol visual en vivo). VS Code no es buena opción para WPF.

### Por qué WPF y cuánto se transfiere

WPF es la tecnología de escritorio .NET con más aplicaciones en producción y más vacantes, y sigue recibiendo mejoras (tema Fluent desde .NET 9). Lo que aprendas aquí (XAML, binding, MVVM) se aplica casi igual en las demás tecnologías XAML:

| Tecnología | Para qué | Plataformas | Cuánto se transfiere desde WPF |
|---|---|---|---|
| WPF | Escritorio Windows; la mayor base instalada | Windows | — |
| Avalonia UI | Escritorio multiplataforma; la más parecida a WPF | Windows, macOS, Linux, móvil, web | Muy alto |
| WinUI 3 (Windows App SDK) | Lo más reciente de Microsoft para escritorio nativo | Windows | Alto |
| Uno Platform | Multiplataforma con la API de WinUI | Windows, macOS, Linux, móvil, web | Alto |
| .NET MAUI | Sobre todo móvil | Android, iOS, Windows, macOS | Medio: mismo MVVM, otros controles |

### WinForms → WPF: el cambio de mentalidad

El error típico es escribir WPF como si fuera WinForms: lógica en el code-behind y controles manipulados a mano. En WPF, la vista se describe en XAML y se **enlaza** a un ViewModel.

| WinForms | WPF |
|---|---|
| Diseñador y eventos en code-behind (`button1_Click`) | XAML + data binding + comandos (`ICommand`) |
| `textBox1.Text = ...` | Propiedad en el ViewModel + `INotifyPropertyChanged` |
| `BindingSource`, `DataTable` | `ObservableCollection<T>`, `CollectionView` |
| Posiciones fijas, `Anchor` y `Dock` | Paneles de layout: `Grid`, `StackPanel`, `DockPanel` |
| Owner-draw o heredar controles para cambiar su aspecto | Estilos, `ControlTemplate`, `DataTemplate` |
| `DataGridView` | `DataGrid`, `ListView`, `ItemsControl` + plantillas |
| `Control.Invoke` / `BeginInvoke` | `Dispatcher` y, sobre todo, async/await |
| `Timer` de WinForms | `DispatcherTimer` |
| `Form` | `Window`, `Page`, `UserControl` |
| `Settings.settings`, `app.config` | `appsettings.json` + Generic Host + options pattern |

### Bloque 1: XAML y layout (1 semana)

- [ ] Sintaxis XAML, namespaces y markup extensions
- [ ] Árbol lógico y árbol visual
- [ ] Paneles de layout y tamaños (`Auto`, `*`, márgenes, alineación)
- [ ] Dependency properties y attached properties
- [ ] Routed events (bubbling y tunneling)
- [ ] Recursos: `StaticResource` vs `DynamicResource`, ResourceDictionary

### Bloque 2: Data binding y MVVM (1–2 semanas)

- [ ] Binding: modos, `UpdateSourceTrigger`, `StringFormat`, converters, `MultiBinding`
- [ ] `INotifyPropertyChanged`, `ObservableCollection<T>`, `CollectionView` (ordenar, filtrar, agrupar)
- [ ] Validación con `INotifyDataErrorInfo`
- [ ] Comandos: `ICommand` y comandos async
- [ ] CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]`, messenger
- [ ] Depurar bindings: ventana "XAML Binding Failures" de Visual Studio y Live Visual Tree
- [ ] Tests unitarios de ViewModels (la gran ventaja de MVVM)

### Bloque 3: Estilos, plantillas y tema moderno (1 semana)

- [ ] Styles, triggers, `ControlTemplate` y `DataTemplate`
- [ ] UserControl vs custom control
- [ ] Tema Fluent nativo (`ThemeMode`, introducido en .NET 9 como API experimental) o la librería WPF-UI; modo claro y oscuro
- [ ] Animaciones básicas y `VisualStateManager`
- [ ] Behaviors (Microsoft.Xaml.Behaviors.Wpf)

### Bloque 4: Arquitectura de la app (1 semana)

- [ ] Generic Host en WPF: DI, configuración y logging
- [ ] Navegación MVVM: ViewModel-first, con un `DataTemplate` por ViewModel
- [ ] Diálogos y notificaciones desde el ViewModel mediante servicios
- [ ] UI responsiva: nunca bloquear el hilo de UI; cancelación y progreso
- [ ] Listas grandes: virtualización

### Bloque 5: Integración con HelpDesk (1 semana)

- [ ] Cliente HTTP tipado (`IHttpClientFactory` o Refit)
- [ ] Login de escritorio con OIDC: navegador del sistema + PKCE (MSAL.NET para Entra ID)
- [ ] Notificaciones en vivo con el cliente de SignalR
- [ ] Caché local y modo offline con EF Core + SQLite

### Bloque 6: Distribución e interoperabilidad (½–1 semana)

- [ ] Publicación self-contained, single-file y ReadyToRun
- [ ] Instaladores y actualizaciones automáticas: MSIX, Velopack o ClickOnce
- [ ] Interop con WinForms: `WindowsFormsHost` (WinForms dentro de WPF) y `ElementHost` (WPF dentro de WinForms), clave para migrar apps poco a poco
- [ ] Bonus: portar una vista a Avalonia y anotar qué cambió

**Entregable:** "Consola del agente" en WPF: lista de tickets con filtros, detalle y respuesta, notificaciones en vivo, tema claro y oscuro, modo offline e instalador.

---

## Cómo cambia la prioridad por sector

| | Corporativo | Startup | Freelance |
|---|---|---|---|
| Base de datos | SQL Server | PostgreSQL | La que tenga el cliente |
| Frontend | Blazor y/o Angular | React | Blazor (rápido para una sola persona) o React |
| Escritorio | WPF: mucho legado WinForms/WPF que mantener y migrar | Poco común | WPF para pymes (punto de venta, inventarios) |
| Nube y auth | Azure, Entra ID, Azure DevOps | Docker, Azure Container Apps o AWS, Keycloak / Auth0 | Hosting barato: VPS con Docker o App Service |
| Kubernetes | AKS en equipos grandes; a menudo App Service o Container Apps | Frecuente | Rara vez: Docker Compose en un VPS suele bastar |
| Arquitectura | Clean Architecture | Vertical Slice | Monolito modular, simple |
| Qué destacar | Tuning de SQL, modernización de legado | Velocidad de entrega, IA | Proyectos desplegados en el portafolio |

## Recursos

- **Documentación oficial** en learn.microsoft.com: ASP.NET Core, EF Core, Blazor, Aspire
- **eShop** (github.com/dotnet/eShop): app de referencia de Microsoft, con Aspire
- **Blog de Andrew Lock** (andrewlock.net): detalles internos de ASP.NET Core
- **WPF:** documentación de WPF y de CommunityToolkit.Mvvm en learn.microsoft.com; WPF-UI (github.com/lepoco/wpfui) para el tema Fluent
- **Docker y Kubernetes:** docs.docker.com, los tutoriales de kubernetes.io y la guía de contenedores .NET en learn.microsoft.com
- **Tuning de SQL:**
  - *How to Think Like the SQL Server Engine*, de Brent Ozar (gratis)
  - *SQL Server Execution Plans*, de Grant Fritchey (ebook gratis de Redgate)
  - *Use The Index, Luke* (use-the-index-luke.com), independiente del motor
  - First Responder Kit de Brent Ozar (open source)

## Bitácora

| Fecha | Fase | Qué hice | Dudas / pendiente |
|---|---|---|---|
| | | | |
