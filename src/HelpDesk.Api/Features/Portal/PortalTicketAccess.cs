using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

// 404 y no 403 cuando el ticket es de otro cliente: un 403 confirmaría que ese Id existe, y como los Id son correlativos, un
// cliente podría recorrerlos para saber cuántos tickets tiene la empresa (y de qué clientes). Para quien pregunta, un ticket
// ajeno y uno inexistente son lo mismo.
public sealed class PortalTicketAccess(ITicketRepository repository)
{
    public async Task<Result<Ticket>> FindMineAsync(TicketId id, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Ticket? ticket = await repository.FindAsync(id, cancellationToken);

        return ticket is not null && string.Equals(ticket.RequestedBy, EmailOf(user), StringComparison.OrdinalIgnoreCase)
            ? ticket
            : Error.NotFound;
    }

    // La policy Customer exige el claim de email, así que aquí siempre está.
    public static string EmailOf(ClaimsPrincipal user) =>
        user.Email ?? throw new InvalidOperationException("La policy Customer garantiza el claim de email.");
}
