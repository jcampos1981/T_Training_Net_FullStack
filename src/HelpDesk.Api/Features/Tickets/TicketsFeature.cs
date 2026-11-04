using HelpDesk.Api.Features.Accounts;
using Microsoft.AspNetCore.Authorization;

namespace HelpDesk.Api.Features.Tickets;

// Lo que comparten los casos de uso de "tickets": sus servicios y su grupo de rutas. Cada caso de uso está en su propio archivo.
public static class TicketsFeature
{
    public static IServiceCollection AddTicketsFeature(this IServiceCollection services)
    {
        // Scoped: un repositorio por petición HTTP, igual que el DbContext que usa por dentro.
        services.AddScoped<ITicketRepository, EfTicketRepository>();
        services.AddScoped<TicketAccess>();
        services.AddScoped<TicketActivityQuery>();

        // Singleton: no guarda estado; la decisión sale del usuario y del ticket que recibe en cada llamada.
        services.AddSingleton<IAuthorizationHandler, TicketAuthorizationHandler>();

        return services;
    }

    public static IEndpointRouteBuilder MapTicketsFeature(this IEndpointRouteBuilder app)
    {
        // Solo el equipo de soporte. Lo fino (qué tickets) lo decide cada caso de uso con TicketAuthorizationHandler.
        RouteGroupBuilder tickets = app.MapGroup("/api/tickets")
            .WithTags("Tickets")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ListTickets.Map(tickets);
        GetTicket.Map(tickets);
        ListTicketActivity.Map(tickets);
        CreateTicket.Map(tickets);
        UpdateTicket.Map(tickets);
        DeleteTicket.Map(tickets);

        return app;
    }
}
