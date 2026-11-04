using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.UnitTests;

internal static class Users
{
    public static ClaimsPrincipal Agent(string name) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, $"{name}@helpdesk.local"),
            new Claim(ClaimTypes.Role, HelpDeskRoles.Agent),
            new Claim(HelpDeskClaimTypes.Agent, name)
        ],
        "prueba"));

    public static ClaimsPrincipal Supervisor() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "laura@helpdesk.local"),
            new Claim(ClaimTypes.Role, HelpDeskRoles.Supervisor)
        ],
        "prueba"));
}

internal static class Authorization
{
    // Las reglas de verdad, no un mock: si este servicio dijera siempre "sí", las pruebas de permisos no probarían nada.
    public static IAuthorizationService Real()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IAuthorizationHandler, TicketAuthorizationHandler>();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }
}

internal static class Tickets
{
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static Ticket Assigned(string? agent, TicketStatus status = TicketStatus.Open) => new()
    {
        Id = new TicketId(7),
        Title = "Impresora sin tóner",
        Priority = TicketPriority.Medium,
        Status = status,
        CreatedAt = Now,
        AssignedTo = AgentName.From(agent),
        Version = 100
    };

    public static UpdateTicketRequest Edit(Ticket ticket, string? title = null, TicketStatus? status = null, string? assignedTo = null) =>
        new(title ?? ticket.Title, ticket.Priority, status ?? ticket.Status, ticket.Version.ToString(), assignedTo ?? ticket.AssignedTo?.Value);
}

internal static class Test
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;
}
