using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HelpDesk.Api.Data;

// Interceptor: EF Core lo llama justo antes de cada SaveChanges, como un middleware de la base de datos.
// Marca la fecha de la última modificación en toda entidad que tenga la columna "UpdatedAt".
// Ojo: ExecuteUpdate/ExecuteDelete NO pasan por SaveChanges, así que ahí hay que ponerla a mano.
public sealed class AuditInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public const string UpdatedAt = "UpdatedAt";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampModifiedEntities(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampModifiedEntities(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampModifiedEntities(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified && entry.Metadata.FindProperty(UpdatedAt) is not null)
            {
                entry.Property(UpdatedAt).CurrentValue = now;
            }
        }
    }
}
