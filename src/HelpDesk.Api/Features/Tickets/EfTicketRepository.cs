using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Features.Tickets;

// Misma interfaz que el repositorio en memoria: los endpoints no se enteran del cambio.
public sealed class EfTicketRepository(HelpDeskDbContext db) : ITicketRepository
{
    public async Task<IReadOnlyList<Ticket>> ListAsync(TicketFilter filter, CancellationToken cancellationToken)
    {
        // IQueryable: cada Where se suma a la consulta y todo se traduce a UN solo SELECT con su WHERE.
        IQueryable<Ticket> query = db.Tickets.AsNoTracking();

        if (filter.Status is { } status)
        {
            query = query.Where(ticket => ticket.Status == status);
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(ticket => ticket.Priority == priority);
        }

        if (filter.AssignedTo is { } agent)
        {
            query = query.Where(ticket => ticket.AssignedTo == agent);
        }

        return await query.OrderBy(ticket => ticket.Id).ToListAsync(cancellationToken);
    }

    // AsNoTracking: solo se lee. Además, como Ticket es inmutable, la versión editada es otra
    // instancia; si la original quedara "rastreada", EF no permitiría adjuntar la nueva con el mismo Id.
    public Task<Ticket?> FindAsync(TicketId id, CancellationToken cancellationToken) =>
        db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket => ticket.Id == id, cancellationToken);

    public async Task<TicketId> NextIdAsync(CancellationToken cancellationToken)
    {
        // SQL directo: el nombre de la secuencia es una constante del código, no un dato del usuario.
        List<int> values = await db.Database
            .SqlQueryRaw<int>($"SELECT NEXT VALUE FOR [{HelpDeskDbContext.TicketIdSequence}] AS [Value]")
            .ToListAsync(cancellationToken);

        return new TicketId(values.Single());
    }

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        db.Tickets.Update(ticket);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ExecuteDeleteAsync: un DELETE directo en la base, sin cargar el ticket antes.
    public async Task<bool> DeleteAsync(TicketId id, CancellationToken cancellationToken) =>
        await db.Tickets.Where(ticket => ticket.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}
