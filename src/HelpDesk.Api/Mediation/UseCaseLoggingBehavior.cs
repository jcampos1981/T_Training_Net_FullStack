using HelpDesk.Domain;

namespace HelpDesk.Api.Mediation;

// Cada rechazo de un caso de uso queda en el log, sin que cada handler tenga que acordarse. Solo el nombre y el tipo de fallo:
// nada de lo que escribió el usuario. En la Fase 11, este es el lugar natural para medir cada caso de uso con OpenTelemetry.
public sealed class UseCaseLoggingBehavior<TRequest, TResponse>(ILogger<UseCaseLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        TResponse response = await next();

        if (response is IFallible { Failure: { } failure })
        {
            string useCase = typeof(TRequest).DeclaringType?.Name ?? typeof(TRequest).Name;
            logger.LogInformation("{UseCase} rechazado: {Kind}", useCase, failure.Kind);
        }

        return response;
    }
}
