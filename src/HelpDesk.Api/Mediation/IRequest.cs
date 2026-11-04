namespace HelpDesk.Api.Mediation;

// Un mediador propio, en vez de MediatR, que pasó a licencia comercial. Son unas 80 líneas: una petición, quien la atiende y lo
// que corre alrededor. La decisión está en docs/adr/0001-vertical-slice.md.
public interface IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

// Lo que corre alrededor de TODOS los casos de uso, sin que cada uno tenga que acordarse. "next" es el siguiente eslabón:
// otro comportamiento, o el handler. Igual que un middleware, pero alrededor de un caso de uso y no de una petición HTTP.
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken);
}

public interface ISender
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken);
}
