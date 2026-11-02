using HelpDesk.Admin.Api.Generated.Models;
using Microsoft.Kiota.Abstractions;

namespace HelpDesk.Admin.Api;

// Qué se le dice al usuario cuando algo falla. Nunca el texto de la excepción: puede traer detalles internos.
public sealed record ApiErrorMessage(string Title, string Detail)
{
    public static ApiErrorMessage From(Exception exception) => exception switch
    {
        ApiException { ResponseStatusCode: StatusCodes.Status403Forbidden } =>
            new("Sin permiso", "Tu cuenta no tiene permiso para ver esta información."),
        // Kiota lanza los errores con cuerpo como ProblemDetails, que hereda de ApiException.
        ProblemDetails { Title: { } title } problem =>
            new(title, problem.Detail ?? "La API rechazó la operación."),
        ApiException { ResponseStatusCode: var status } =>
            new($"Error {status}", "La API no pudo completar la operación."),
        HttpRequestException =>
            new("No se pudo conectar", "La API no responde. Revisa que esté corriendo e inténtalo de nuevo."),
        _ =>
            new("Error inesperado", "Algo salió mal. Inténtalo de nuevo.")
    };
}
