using HelpDesk.Api.Features.Accounts;

namespace HelpDesk.Api.Features.Portal;

// El portal del cliente: abrir tickets, seguir los propios y conversar en ellos. Otra audiencia que /api/tickets,
// con otras reglas y otro contrato; por eso es otra feature y no un "if (esCliente)" dentro de la de soporte.
public static class PortalFeature
{
    public static IServiceCollection AddPortalFeature(this IServiceCollection services)
    {
        services.AddScoped<PortalTicketAccess>();
        return services;
    }

    public static IEndpointRouteBuilder MapPortalFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder portal = app.MapGroup("/api/portal/tickets")
            .WithTags("Portal del cliente")
            .RequireAuthorization(HelpDeskPolicies.Customer)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ListMyTickets.Map(portal);
        GetMyTicket.Map(portal);
        OpenTicket.Map(portal);
        ListMyTicketComments.Map(portal);
        ReplyToMyTicket.Map(portal);

        return app;
    }
}
