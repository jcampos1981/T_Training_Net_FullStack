using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Data;

// Carga datos de ejemplo en Development. Cada tabla se siembra solo si está vacía, y las escrituras
// pasan por los repositorios y el dominio (SEQUENCE para tickets, reglas para comentarios).
public static class DevelopmentSeeder
{
    private static readonly (string Author, string Body)[] SampleComments =
    [
        ("ana", "Le pedí al usuario una captura del error."),
        ("luis", "Lo reproduje en el ambiente de pruebas."),
        ("marta", "Escalado con el proveedor; esperamos respuesta.")
    ];

    public static async Task SeedDevelopmentDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // El DbContext es Scoped (uno por petición): fuera de una petición hay que crear el scope a mano.
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        HelpDeskDbContext db = provider.GetRequiredService<HelpDeskDbContext>();
        DateTimeOffset now = provider.GetRequiredService<TimeProvider>().GetUtcNow();

        if (!await db.Tickets.AnyAsync(cancellationToken))
        {
            await SeedTicketsAsync(provider.GetRequiredService<ITicketRepository>(), now, cancellationToken);
        }

        if (!await db.Comments.AnyAsync(cancellationToken))
        {
            await SeedCommentsAsync(db, provider.GetRequiredService<ICommentRepository>(), now, cancellationToken);
        }
    }

    private static async Task SeedTicketsAsync(ITicketRepository tickets, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (Ticket sample in TicketSeed.Create(now))
        {
            TicketId id = await tickets.NextIdAsync(cancellationToken);
            await tickets.AddAsync(sample with { Id = id }, cancellationToken);
        }
    }

    // Entre 1 y 3 comentarios por ticket abierto, para que la consulta de actividad tenga datos variados.
    private static async Task SeedCommentsAsync(
        HelpDeskDbContext db,
        ICommentRepository comments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<Ticket> openTickets = await db.Tickets.AsNoTracking()
            .Where(ticket => ticket.Status == TicketStatus.Open || ticket.Status == TicketStatus.InProgress)
            .OrderBy(ticket => ticket.Id)
            .ToListAsync(cancellationToken);

        for (int ticketIndex = 0; ticketIndex < openTickets.Count; ticketIndex++)
        {
            int commentCount = ticketIndex % SampleComments.Length + 1;

            for (int commentIndex = 0; commentIndex < commentCount; commentIndex++)
            {
                (string author, string body) = SampleComments[commentIndex];
                DateTimeOffset createdAt = now.AddMinutes(-30 * (commentCount - commentIndex));

                await comments.AddAsync(openTickets[ticketIndex].CreateComment(author, body, createdAt), cancellationToken);
            }
        }
    }
}
