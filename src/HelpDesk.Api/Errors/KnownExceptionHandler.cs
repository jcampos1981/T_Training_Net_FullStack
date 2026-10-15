using HelpDesk.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.Api.Errors;

// Traduce las excepciones "esperadas" a respuestas ProblemDetails (RFC 9457).
// Las demás no se tocan: el manejador por defecto responde 500 sin revelar detalles internos.
public sealed class KnownExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int Status, string Title)? mapping = exception switch
        {
            DomainRuleException => (StatusCodes.Status409Conflict, "Regla de negocio incumplida"),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Solicitud inválida"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Datos inválidos"),
            _ => null
        };

        if (mapping is not (var status, var title))
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                // Para JSON mal formado el detalle útil viene en la excepción interna (JsonException).
                Detail = exception.InnerException?.Message ?? exception.Message
            }
        });
    }
}
