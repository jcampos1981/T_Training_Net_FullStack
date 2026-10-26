using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HelpDesk.Api.Features.Agents;

// Caso de uso con varios pasos que deben ocurrir todos o ninguno:
// 1) leer los tickets afectados  2) reasignarlos con un UPDATE masivo  3) dejar un comentario en cada uno.
public sealed class ReassignTicketsHandler(
    HelpDeskDbContext db,
    ITicketRepository tickets,
    TimeProvider clock)
{
    private const string SystemAuthor = "sistema";

    public async Task<ReassignTicketsResponse> HandleAsync(string fromAgent, string toAgent, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // ExecuteUpdate se ejecuta en el acto (no espera a SaveChanges): sin una transacción explícita,
        // si después fallara el guardado de los comentarios, la reasignación ya habría quedado hecha.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        List<Ticket> affected = await db.Tickets.AsNoTracking()
            .Where(ticket => ticket.AssignedTo == fromAgent
                && (ticket.Status == TicketStatus.Open || ticket.Status == TicketStatus.InProgress))
            .ToListAsync(cancellationToken);

        int reassigned = await tickets.ReassignOpenTicketsAsync(fromAgent, toAgent, now, cancellationToken);

        // Si otro usuario cambió esos tickets entre la lectura y el UPDATE, los números no cuadran:
        // se lanza la excepción, el "await using" deshace la transacción (rollback) y la API responde 409.
        if (reassigned != affected.Count)
        {
            throw new DbUpdateConcurrencyException("Los tickets del agente cambiaron durante la reasignación.");
        }

        db.Comments.AddRange(affected.Select(ticket =>
            ticket.CreateComment(SystemAuthor, $"Reasignado de {fromAgent} a {toAgent}.", now)));
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ReassignTicketsResponse(fromAgent, toAgent, reassigned);
    }
}
