using System.Reflection;

namespace HelpDesk.Api.Mediation;

public static class MediationSetup
{
    // Busca los handlers en el ensamblado: un caso de uso nuevo queda registrado solo con existir.
    public static IServiceCollection AddMediation(this IServiceCollection services, Assembly assembly)
    {
        services.AddScoped<ISender, Sender>();
        // El primero que se registra envuelve a los demás: la medición incluye todo lo que corre alrededor del caso de uso.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(UseCaseTelemetryBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(UseCaseLoggingBehavior<,>));

        IEnumerable<(Type Handler, Type Contract)> handlers =
            from type in assembly.GetTypes()
            where type is { IsClass: true, IsAbstract: false }
            from contract in type.GetInterfaces()
            where contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
            select (type, contract);

        foreach ((Type handler, Type contract) in handlers)
        {
            services.AddScoped(contract, handler);
        }

        return services;
    }
}
