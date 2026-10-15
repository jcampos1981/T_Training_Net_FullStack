using System.Collections.Concurrent;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Una sola instancia atiende peticiones simultáneas: ConcurrentDictionary e Interlocked
// evitan que dos peticiones a la vez corrompan los datos o reciban el mismo Id.
public sealed class InMemoryTicketRepository : ITicketRepository
{
    private readonly ConcurrentDictionary<TicketId, Ticket> _tickets;
    private int _lastId;

    public InMemoryTicketRepository(IEnumerable<Ticket> seed)
    {
        _tickets = new(seed.Select(ticket => KeyValuePair.Create(ticket.Id, ticket)));
        _lastId = _tickets.Keys.Select(id => id.Value).DefaultIfEmpty().Max();
    }

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

    public Task<TicketId> NextIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new TicketId(Interlocked.Increment(ref _lastId)));

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (!_tickets.TryAdd(ticket.Id, ticket))
        {
            throw new InvalidOperationException($"Ya existe un ticket con Id {ticket.Id}.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        _tickets[ticket.Id] = ticket;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(TicketId id, CancellationToken cancellationToken) =>
        Task.FromResult(_tickets.TryRemove(id, out _));
}
