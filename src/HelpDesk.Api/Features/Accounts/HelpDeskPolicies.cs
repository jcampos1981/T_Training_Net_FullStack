using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace HelpDesk.Api.Features.Accounts;

// Policies con nombre: los endpoints piden "Staff" o "Supervision", no una lista de roles. Si mañana cambia quién
// puede supervisar, se cambia aquí y no en cada endpoint.
public static class HelpDeskPolicies
{
    // El equipo de soporte: agentes, supervisores y admins. Un usuario registrado sin rol queda afuera.
    public const string Staff = "Staff";

    // Lo que mira o mueve el trabajo de todo el equipo: dashboard, reasignaciones, borrar tickets.
    public const string Supervision = "Supervision";

    // El portal del cliente (/api/portal). Un cliente no entra a /api/tickets: no es Staff.
    public const string Customer = "Customer";

    public static AuthorizationBuilder AddHelpDeskPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(Staff, policy => policy.RequireRole(HelpDeskRoles.Agent, HelpDeskRoles.Supervisor, HelpDeskRoles.Admin))
            .AddPolicy(Supervision, policy => policy.RequireRole(HelpDeskRoles.Supervisor, HelpDeskRoles.Admin))
            .AddPolicy(Customer, policy => policy.RequireRole(HelpDeskRoles.Customer).RequireClaim(ClaimTypes.Email));
}
