using HelpDesk.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Errors;

// Traduce las excepciones "esperadas" a respuestas ProblemDetails (RFC 9457).
// Las demás no se tocan: el manejador por defecto responde 500 sin revelar detalles internos.
public sealed class KnownExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IOptions<ApiErrorOptions> options) : IExceptionHandler
{
    private const string GenericBadRequestDetail = "La petición no tiene el formato o los datos esperados.";

    private const string ConcurrencyDetail =
        "Alguien más modificó este registro desde que lo consultaste. Vuelve a cargarlo y aplica tus cambios de nuevo.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int Status, string Title, string Detail)? mapping = exception switch
        {
            // Los mensajes de reglas de negocio están escritos para el usuario: se muestran siempre.
            DomainRuleException => (StatusCodes.Status409Conflict, "Regla de negocio incumplida", exception.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflicto de edición", ConcurrencyDetail),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Solicitud inválida", TechnicalOrGenericDetail(exception)),
            ArgumentException => (StatusCodes.Status400BadRequest, "Datos inválidos", TechnicalOrGenericDetail(exception)),
            _ => null
        };

        if (mapping is not (var status, var title, var detail))
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }

    // Para JSON mal formado, el mensaje útil viene en la excepción interna (JsonException).
    private string TechnicalOrGenericDetail(Exception exception) =>
        options.Value.ExposeTechnicalDetails
            ? exception.InnerException?.Message ?? exception.Message
            : GenericBadRequestDetail;
}
