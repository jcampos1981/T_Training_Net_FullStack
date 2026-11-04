# ADR 0001: Vertical Slice, con Result y un mediador propio

**Estado:** aceptada, noviembre de 2026.

## Contexto

HelpDesk tiene un proyecto de dominio y una API con unos 30 casos de uso. El código ya estaba agrupado por funcionalidad en `Features/`, pero cada una tenía un archivo grande con todos sus endpoints y mezclaba HTTP con reglas de negocio. `TicketsFeature.cs` llegó a 235 líneas.

Había que elegir hacia dónde ordenarlo: **Clean Architecture** o **Vertical Slice**.

## Decisión

**Vertical Slice:** un archivo por caso de uso (`Features/Tickets/UpdateTicket.cs`), con su petición, su handler y su endpoint.

| Criterio | Clean Architecture | Vertical Slice | Para HelpDesk |
|---|---|---|---|
| Se organiza por | Capa técnica | Caso de uso | Los cambios suelen tocar un solo caso de uso |
| Agregar un caso de uso | Tocar 3 o 4 proyectos | Un archivo nuevo | Aquí, casi todos los cambios son de este tipo |
| Consultas especiales | Pasan por una abstracción | Cada una usa lo que le sirve | Ya conviven EF Core y SQL a mano por rendimiento (Fase 4) |
| Protege el dominio | Sí, con proyectos separados | Sí, si el dominio es su propio proyecto | `HelpDesk.Domain` ya existe, y una prueba de arquitectura lo vigila |
| Cambiar de base de datos | Se promete, y casi nunca ocurre | No se promete | La Fase 3 mostró que SQL a mano y `rowversion` no viajan igual |

**Lo que se pierde:** nada impide por compilación que un caso de uso use directamente lo que quiera. Lo vigilan las pruebas de arquitectura (`tests/HelpDesk.ArchitectureTests`).

**Cuándo revisar esta decisión:** si varias aplicaciones llegaran a compartir los mismos casos de uso, o si un equipo distinto se hiciera cargo de la infraestructura.

## Reglas que se siguen

**1. Un Handler solo si hay reglas.** Una lectura que no puede fallar (`ListMyTickets`, `/api/agents`) le pregunta directo a su consulta: un Handler ahí solo sería una capa más.

**2. Fallos esperados con `Result`, y lo inesperado con excepciones.**

| Situación | Cómo se expresa | Por qué |
|---|---|---|
| Ticket cerrado, sin permiso, no existe, conflicto de edición | `Result<T>` con un `Error` | Es una respuesta posible: la firma lo dice y quien llama tiene que mirarlo |
| La base está caída, un bug | Excepción | No hay nada razonable que hacer ahí mismo |
| Un invariante roto, como un título vacío al crear un `Ticket` | Excepción | El dominio nunca debe quedar inválido, y la validación ya lo filtró antes |

`DbUpdateConcurrencyException` no sale del repositorio: es un detalle de EF. El único lugar que traduce un `Error` a HTTP es `ResultHttpExtensions`.

**3. Mediador propio, en unas 80 líneas** (`src/HelpDesk.Api/Mediation`).

MediatR pasó a licencia comercial, y aquí alcanza con poco. Lo que aporta es el pipeline: lo que corre alrededor de todos los casos de uso sin que cada uno tenga que acordarse. Hoy registra los rechazos, y en la Fase 11 medirá cada caso de uso.

**El costo es real:** "Ir a la definición" sobre `SendAsync` no lleva al handler. Si el pipeline no terminara usándose, lo correcto sería quitarlo e inyectar cada handler en su endpoint.

**4. Mapeo a mano.** `ticket.ToResponse(sla)` es explícito, lo revisa el compilador y no depende de nadie. AutoMapper pasó a licencia comercial, y con este tamaño tampoco se justifica Mapperly.

## Cómo se verificó

El documento OpenAPI quedó idéntico antes y después, y las pruebas de integración no se tocaron: la refactorización cambió la estructura, no el comportamiento.
