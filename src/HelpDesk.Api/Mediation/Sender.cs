using System.Collections.Concurrent;

namespace HelpDesk.Api.Mediation;

public sealed class Sender(IServiceProvider services) : ISender
{
    // Crear el tipo genérico por reflexión es lento, y solo hace falta una vez por cada tipo de petición.
    private static readonly ConcurrentDictionary<Type, object> Dispatchers = new();

    public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var dispatcher = (IDispatcher<TResponse>)Dispatchers.GetOrAdd(
            request.GetType(),
            static requestType => Activator.CreateInstance(typeof(Dispatcher<,>).MakeGenericType(requestType, typeof(TResponse)))!);

        return dispatcher.DispatchAsync(request, services, cancellationToken);
    }

    private interface IDispatcher<TResponse>
    {
        Task<TResponse> DispatchAsync(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class Dispatcher<TRequest, TResponse> : IDispatcher<TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> DispatchAsync(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken)
        {
            var typedRequest = (TRequest)request;
            var handler = services.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

            Func<Task<TResponse>> pipeline = () => handler.HandleAsync(typedRequest, cancellationToken);

            // Se arma de adentro hacia afuera: el primer comportamiento registrado queda como el más externo.
            foreach (IPipelineBehavior<TRequest, TResponse> behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                Func<Task<TResponse>> next = pipeline;
                pipeline = () => behavior.HandleAsync(typedRequest, next, cancellationToken);
            }

            return pipeline();
        }
    }
}
