using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HelpDesk.Api.Features.Agents;

// Caso de uso con dos pasos que deben ocurrir todos o ninguno: dejar un comentario en cada ticket abierto
// del agente y reasignarlos. Los dos son sentencias masivas: la base hace el trabajo, sin traer los tickets.
public sealed class ReassignTicketsHandler(
    HelpDeskDbContext db,
    ITicketRepository tickets,
    ICommentRepository comments,
    TimeProvider clock)
{
    private const string SystemAuthor = "sistema";

    public Task<ReassignTicketsResponse> HandleAsync(string fromAgent, string toAgent, CancellationToken cancellationToken)
    {
        // Con reintentos (EnableRetryOnFailure), una transacción propia tiene que ir dentro de la estrategia:
        // si SQL Server la elige víctima de un deadlock (error 1205), se repite COMPLETA.
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(attempt => ReassignAsync(fromAgent, toAgent, attempt), cancellationToken);
    }

    private async Task<ReassignTicketsResponse> ReassignAsync(string fromAgent, string toAgent, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // Las sentencias masivas se ejecutan en el acto (no esperan a SaveChanges): sin una transacción
        // explícita, si fallara la segunda, la primera ya habría quedado hecha.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Primero los comentarios: después del UPDATE, los tickets movidos ya no se distinguen de los que
        // el agente destino tenía antes.
        int commented = await comments.AddToOpenTicketsAsync(
            fromAgent, SystemAuthor, $"Reasignado de {fromAgent} a {toAgent}.", now, cancellationToken);

        int reassigned = await tickets.ReassignOpenTicketsAsync(fromAgent, toAgent, now, cancellationToken);

        // Las dos sentencias eligen los tickets con el mismo WHERE. Si otro usuario cambió alguno entre una y
        // otra, los números no cuadran: se lanza la excepción, el "await using" deshace la transacción y la API
        // responde 409. No es un error transitorio, así que la estrategia NO lo reintenta.
        if (reassigned != commented)
        {
            throw new DbUpdateConcurrencyException("Los tickets del agente cambiaron durante la reasignación.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new ReassignTicketsResponse(fromAgent, toAgent, reassigned);
    }
}
