using System.Text.Json;
using HelpDesk.Api.Features.Accounts;

namespace HelpDesk.Api.Features.Notifications;

// Avisos en vivo con SignalR: la API le avisa a cada pantalla abierta que algo cambió, sin que tenga que preguntar.
public static class NotificationsFeature
{
    private const string TicketEventsPath = "/hubs/tickets";

    public static IServiceCollection AddNotificationsFeature(this IServiceCollection services, Action<JsonSerializerOptions> configureJson)
    {
        // El mismo JSON que el resto de la API: enums como texto ("Closed" y no 3).
        services.AddSignalR().AddJsonProtocol(options => configureJson(options.PayloadSerializerOptions));
        services.AddSingleton<ITicketNotifier, SignalRTicketNotifier>();

        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsFeature(this IEndpointRouteBuilder app)
    {
        // Un WebSocket se autentica una sola vez, al conectarse: sin esto seguiría abierto, y recibiendo avisos, aunque
        // el token haya vencido o el usuario haya perdido el acceso. Así se cierra cuando vence el token; el cliente se
        // reconecta con uno nuevo, y si la sesión ya no existe, no puede.
        app.MapHub<TicketEventsHub>(TicketEventsPath, options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(HelpDeskPolicies.LiveEvents);

        return app;
    }
}
