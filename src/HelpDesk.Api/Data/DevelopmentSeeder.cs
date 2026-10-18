using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;

namespace HelpDesk.Api.Data;

// Carga tickets de ejemplo en Development cuando la tabla está vacía. Usa el mismo repositorio
// que la API, así los Ids también salen de la SEQUENCE.
public static class DevelopmentSeeder
{
    private static readonly TicketFilter AllTickets = new(Status: null, Priority: null, AssignedTo: null);

    public static async Task SeedDevelopmentDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // El DbContext es Scoped (uno por petición): fuera de una petición hay que crear el scope a mano.
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ITicketRepository repository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();
        TimeProvider clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        if ((await repository.ListAsync(AllTickets, cancellationToken)).Count > 0)
        {
            return;
        }

        foreach (Ticket sample in TicketSeed.Create(clock.GetUtcNow()))
        {
            TicketId id = await repository.NextIdAsync(cancellationToken);
            await repository.AddAsync(sample with { Id = id }, cancellationToken);
        }
    }
}
