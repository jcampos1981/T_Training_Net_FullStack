using HelpDesk.Domain;
using HelpDesk.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HelpDesk.Api.Outbox;

// Patrón outbox. Guardar el ticket y publicar en RabbitMQ son dos sistemas, y no hay una transacción que abarque ambos: si se cae algo entre
// medio, el ticket queda sin evento, o el evento sin ticket. En cambio, escribir el evento en una tabla sí es parte del mismo SaveChanges.
//
// Aquí, y no en cada caso de uso: así no hay forma de crear o cerrar un ticket y olvidarse del evento. Ojo: ExecuteUpdate y ExecuteDelete no
// pasan por SaveChanges. Ninguno cambia el estado de un ticket, pero uno que lo haga tendría que escribir su evento a mano.
public sealed class OutboxInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AddEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddEvents(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        OutboxMessage[] messages =
        [
            .. from entry in context.ChangeTracker.Entries<Ticket>()
               let type = EventTypeOf(entry)
               where type is not null
               select OutboxMessage.For(type, entry.Entity, now)
        ];

        context.AddRange(messages);
    }

    private static string? EventTypeOf(EntityEntry<Ticket> entry) => entry.State switch
    {
        EntityState.Added => TicketEventType.Created,
        EntityState.Modified when BecameClosed(entry.Property(ticket => ticket.Status)) => TicketEventType.Closed,
        _ => null
    };

    private static bool BecameClosed(PropertyEntry<Ticket, TicketStatus> status) =>
        status.OriginalValue is not TicketStatus.Closed && status.CurrentValue is TicketStatus.Closed;
}
