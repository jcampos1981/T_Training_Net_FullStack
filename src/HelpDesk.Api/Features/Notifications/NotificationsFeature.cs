using System.Text.Json;
using HelpDesk.Api.Features.Accounts;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace HelpDesk.Api.Features.Notifications;

// Avisos en vivo con SignalR: la API le avisa a cada pantalla abierta que algo cambió, sin que tenga que preguntar.
public static class NotificationsFeature
{
    private const string TicketEventsPath = "/hubs/tickets";
    private const string RedisChannelPrefix = "helpdesk";

    public static IServiceCollection AddNotificationsFeature(
        this IServiceCollection services,
        Action<JsonSerializerOptions> configureJson,
        ConfigurationOptions? redis)
    {
        // El mismo JSON que el resto de la API: enums como texto ("Closed" y no 3).
        ISignalRServerBuilder signalR = services.AddSignalR().AddJsonProtocol(options => configureJson(options.PayloadSerializerOptions));

        // Backplane: cada instancia solo conoce a sus propios clientes. Con Redis, un aviso que sale de una instancia llega
        // a los conectados a cualquier otra. Sin él, quien esté en otra instancia no se enteraría.
        if (redis is not null)
        {
            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = redis.Clone();
                options.Configuration.ChannelPrefix = RedisChannel.Literal(RedisChannelPrefix);
            });
        }

        services.AddSingleton<ITicketNotifier, SignalRTicketNotifier>();

        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsFeature(this IEndpointRouteBuilder app)
    {
        // Un WebSocket se autentica una sola vez, al conectarse: sin esto seguiría abierto, y recibiendo avisos, aunque
        // el token haya vencido o el usuario haya perdido el acceso. Así se cierra cuando vence el token; el cliente se
        // reconecta con uno nuevo, y si la sesión ya no existe, no puede.
        app.MapHub<TicketEventsHub>(TicketEventsPath, options => options.CloseOnAuthenticationExpiration = true)
            .DisableRequestTimeout() // una conexión dura horas: el límite es para las peticiones normales
            .RequireAuthorization(HelpDeskPolicies.LiveEvents);

        return app;
    }
}
