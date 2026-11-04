using System.Security.Claims;
using HelpDesk.Api.Caching;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HelpDesk.Api.Features.Agents;

public static class ReassignTickets
{
    public sealed record Command(string FromAgent, string ToAgent, ClaimsPrincipal User) : IRequest<Result<ReassignTicketsResponse>>;

    // Dos pasos que deben ocurrir todos o ninguno: dejar un comentario en cada ticket abierto del agente y reasignarlos. Los dos
    // son sentencias masivas: la base hace el trabajo, sin traer los tickets.
    public sealed class Handler(
        HelpDeskDbContext db,
        ITicketRepository tickets,
        ICommentRepository comments,
        ITicketNotifier notifier,
        IOutputCacheStore outputCache,
        TimeProvider clock) : IRequestHandler<Command, Result<ReassignTicketsResponse>>
    {
        private const string SystemAuthor = "sistema";

        public async Task<Result<ReassignTicketsResponse>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            // Con reintentos (EnableRetryOnFailure), una transacción propia tiene que ir dentro de la estrategia:
            // si SQL Server la elige víctima de un deadlock (error 1205), se repite COMPLETA.
            IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
            Result<ReassignTicketsResponse> result = await strategy.ExecuteAsync(
                attempt => ReassignAsync(command.FromAgent, command.ToAgent, attempt), cancellationToken);

            if (!result.IsSuccess)
            {
                return result;
            }

            // Quien reasigna vuelve a pedir la lista enseguida: debe ver el resultado, no lo guardado hace 4 segundos.
            await outputCache.EvictByTagAsync(AgentSummaryCache.Tag, cancellationToken);
            await notifier.TicketsReassignedAsync(result.Value, command.User);

            return result;
        }

        private async Task<Result<ReassignTicketsResponse>> ReassignAsync(string fromAgent, string toAgent, CancellationToken cancellationToken)
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

            // Las dos sentencias eligen los tickets con el mismo WHERE. Si otro usuario cambió alguno entre una y otra, los números
            // no cuadran: el "await using" deshace la transacción al salir, sin confirmar nada.
            if (reassigned != commented)
            {
                return Error.EditConflict;
            }

            await transaction.CommitAsync(cancellationToken);

            return new ReassignTicketsResponse(fromAgent, toAgent, reassigned);
        }
    }
}
