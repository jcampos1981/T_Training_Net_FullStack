using System.Security.Claims;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace HelpDesk.Api.Features.Tickets;

// "Buscar el ticket y revisar que este usuario pueda hacer esto con él": lo necesitan casi todos los casos de uso del equipo.
//
// 403 y no 404 para el ticket de otro agente: dentro del equipo, que un ticket exista no es un secreto (los Id son
// correlativos). Para clientes, en cambio, es 404: ver PortalTicketAccess.
public sealed class TicketAccess(ITicketRepository repository, IAuthorizationService authorization)
{
    public async Task<Result<Ticket>> FindAsync(
        TicketId id,
        ClaimsPrincipal user,
        OperationAuthorizationRequirement operation,
        CancellationToken cancellationToken)
    {
        Ticket? ticket = await repository.FindAsync(id, cancellationToken);

        if (ticket is null)
        {
            return Error.NotFound;
        }

        return (await authorization.AuthorizeAsync(user, ticket, operation)).Succeeded ? ticket : Error.Forbidden;
    }
}
