using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Paging;

// Paginación por keyset ("los siguientes N después del Id X"): ?after=120&pageSize=50.
// A diferencia de OFFSET, no recorre las filas de las páginas anteriores: cuesta lo mismo en la página 1 que en la 10,000.
//
// Propiedades init y NO un record posicional, a propósito: así cada [Range] vive en un solo lugar y lo entienden
// los dos lados de la API. En un record posicional, MVC (AgentsController) exige la validación en el parámetro del
// constructor, y justo ahí el generador de OpenAPI de Minimal APIs ([AsParameters], ASP.NET Core 10.0.13) falla:
// /openapi/v1.json respondía 500.
public sealed record PageRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    [Range(0, int.MaxValue, ErrorMessage = "after no puede ser negativo.")]
    public int After { get; init; }

    [Range(1, MaxPageSize, ErrorMessage = "pageSize debe estar entre {1} y {2}.")]
    public int PageSize { get; init; } = DefaultPageSize;

    // Se pide UNA fila de más: si llega, hay otra página, sin necesidad de un COUNT(*).
    public int Take() => PageSize + 1;
}
