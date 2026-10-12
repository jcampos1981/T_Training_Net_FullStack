using System.Collections.Concurrent;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Una sola instancia atiende peticiones simultáneas: ConcurrentDictionary evita corromper los datos.
public sealed class InMemoryTicketRepository(IEnumerable<Ticket> seed) : ITicketRepository
{
    private readonly ConcurrentDictionary<TicketId, Ticket> _tickets =
        new(seed.Select(ticket => KeyValuePair.Create(ticket.Id, ticket)));

    public Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken)
    {
        IReadOnlyList<Ticket> tickets = _tickets.Values
            .Where(filter.Matches)
            .OrderBy(ticket => ticket.Id.Value)
            .ToList();

        return Task.FromResult(tickets);
    }

    public Task<Ticket?> FindAsync(TicketId id, CancellationToken cancellationToken) =>
        Task.FromResult(_tickets.GetValueOrDefault(id));
}
