using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Paging;

// Paginación por keyset ("los siguientes N después del Id X"): ?after=120&pageSize=50.
// A diferencia de OFFSET, no recorre las filas de las páginas anteriores: cuesta lo mismo en la página 1 que en la 10,000.
//
// Record posicional con valores por defecto: así Minimal APIs ([AsParameters]) y MVC tratan los dos parámetros como
// opcionales. La validación va en Validate() y no en [Range], porque los atributos chocan en ASP.NET Core 10.0.13:
// - en el parámetro del constructor, rompen el documento OpenAPI de Minimal APIs (/openapi/v1.json → 500);
// - en la propiedad, MVC lanza una excepción (en un record exige el atributo en el parámetro);
// - y con propiedades init en vez de record posicional, [AsParameters] vuelve OBLIGATORIOS a after y pageSize.
// IValidatableObject lo entienden las dos validaciones (MVC y AddValidation de Minimal APIs).
public sealed record PageRequest(int After = 0, int PageSize = PageRequest.DefaultPageSize) : IValidatableObject
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Se pide UNA fila de más: si llega, hay otra página, sin necesidad de un COUNT(*).
    public int Take() => PageSize + 1;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (After < 0)
        {
            yield return new ValidationResult("after no puede ser negativo.", [nameof(After)]);
        }

        if (PageSize is < 1 or > MaxPageSize)
        {
            yield return new ValidationResult($"pageSize debe estar entre 1 y {MaxPageSize}.", [nameof(PageSize)]);
        }
    }
}
