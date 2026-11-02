using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Errors;

// La validación nombra cada error con la propiedad de C# ("Title"), pero el cliente mandó "title" en el JSON. Aquí se
// renombran las claves con la misma política de nombres del JSON: así el frontend muestra cada mensaje junto al campo
// que lo causó, sin adivinar mayúsculas. Aplica a los 400 de Minimal APIs (AddValidation) y de Controllers
// (ModelState), porque los dos pasan por ProblemDetailsOptions.CustomizeProblemDetails. .NET 10 no trae una opción
// para esto: su documentación muestra las claves con la ruta de C# ("Customer.ShippingAddress.Street").
// Efecto lateral aceptado: /api/auth/register (Identity) usa códigos como claves ("PasswordTooShort"), y también
// quedan en camelCase ("passwordTooShort"). La regla es pareja: toda clave de "errors" va en camelCase.
public static class ValidationErrorKeys
{
    public static void UseJsonPropertyNames(ProblemDetailsContext context)
    {
        if (context.ProblemDetails is not HttpValidationProblemDetails { Errors.Count: > 0 } validation)
        {
            return;
        }

        // Minimal APIs y Controllers tienen opciones JSON separadas, pero Program.cs configura las dos con la misma
        // función (ConfigureJson): basta leer una.
        JsonNamingPolicy? naming = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions.PropertyNamingPolicy;
        if (naming is null)
        {
            return;
        }

        var renamed = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach ((string key, string[] messages) in validation.Errors)
        {
            string jsonKey = ToJsonPath(key, naming);
            renamed[jsonKey] = renamed.TryGetValue(jsonKey, out string[]? previous) ? [.. previous, .. messages] : messages;
        }

        validation.Errors = renamed;
    }

    // "Items[0].UnitPrice" → "items[0].unitPrice": cada tramo del camino se convierte, los índices quedan igual.
    private static string ToJsonPath(string key, JsonNamingPolicy naming) =>
        string.Join('.', key.Split('.').Select(segment => ToJsonName(segment, naming)));

    private static string ToJsonName(string segment, JsonNamingPolicy naming)
    {
        int indexer = segment.IndexOf('[');
        return indexer < 0
            ? naming.ConvertName(segment)
            : naming.ConvertName(segment[..indexer]) + segment[indexer..];
    }
}
