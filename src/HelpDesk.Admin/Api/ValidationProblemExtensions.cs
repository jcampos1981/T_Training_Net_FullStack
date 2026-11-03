using Microsoft.Kiota.Abstractions.Serialization;
// El tipo que genera Kiota, no el del mismo nombre que ASP.NET Core trae en sus usings globales.
using HttpValidationProblemDetails = HelpDesk.Admin.Api.Generated.Models.HttpValidationProblemDetails;

namespace HelpDesk.Admin.Api;

public static class ValidationProblemExtensions
{
    // El contrato de "errors" es un diccionario con claves libres (campo → mensajes), así que Kiota no le puede dar
    // propiedades: lo deja en AdditionalData, con los valores sin tipar. Aquí se convierte a lo que de verdad es.
    public static IReadOnlyDictionary<string, string[]> FieldErrors(this HttpValidationProblemDetails problem) =>
        problem.Errors?.AdditionalData.ToDictionary(entry => entry.Key, entry => MessagesIn(entry.Value))
        ?? new Dictionary<string, string[]>();

    private static string[] MessagesIn(object value) => value switch
    {
        UntypedArray messages => [.. messages.GetValue().OfType<UntypedString>().Select(message => message.GetValue() ?? string.Empty)],
        _ => [value.ToString() ?? string.Empty]
    };
}
