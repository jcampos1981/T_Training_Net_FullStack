using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Paging;

// Paginación por keyset ("los siguientes N después del Id X"): ?after=120&pageSize=50.
// A diferencia de OFFSET, no recorre las filas de las páginas anteriores: cuesta lo mismo en la página 1 que en la 10,000.
public sealed record PageRequest(
    [Range(0, int.MaxValue, ErrorMessage = "after no puede ser negativo.")]
    int After = 0,
    [Range(1, PageRequest.MaxPageSize, ErrorMessage = "pageSize debe estar entre {1} y {2}.")]
    int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Se pide UNA fila de más: si llega, hay otra página, sin necesidad de un COUNT(*).
    public int Take() => PageSize + 1;
}
