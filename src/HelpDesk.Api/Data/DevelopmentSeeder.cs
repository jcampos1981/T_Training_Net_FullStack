using System.Security.Claims;
using HelpDesk.Api.Features.Accounts;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.Api.Data;

// Carga datos de ejemplo en Development. Cada tabla se siembra solo si está vacía, y las escrituras
// pasan por los repositorios y el dominio (SEQUENCE para tickets, reglas para comentarios).
public static class DevelopmentSeeder
{
    // La contraseña de los usuarios de desarrollo viene de User Secrets, nunca del repositorio:
    //   dotnet user-secrets set "DevelopmentUsers:Password" "<contraseña>" --project src/HelpDesk.Api
    public const string PasswordKey = "DevelopmentUsers:Password";

    private const string EmailDomain = "helpdesk.local";

    // Los clientes son de otra empresa: su email no es del dominio del HelpDesk.
    private const string CustomerEmailDomain = "acme.local";

    // Un usuario por cada agente que aparece en Ticket.AssignedTo.
    private static readonly string[] AgentNames = ["pedro", "luis", "marta", "sofia", "jorge", "carlos", "elena", "ana"];

    private static readonly string[] CustomerNames = ["camila", "diego"];

    private static readonly (string Customer, OpenTicketRequest Request)[] SamplePortalTickets =
    [
        ("camila", new("No puedo entrar al sistema de facturación", "Desde esta mañana el login me dice que la clave es incorrecta, pero no la he cambiado.", TicketPriority.High)),
        ("camila", new("Solicitud de una cuenta nueva", "Ingresa una persona al área de contabilidad el lunes y necesita acceso al ERP.", TicketPriority.Low)),
        ("camila", new("La impresora del piso 3 imprime en blanco", "Imprime las hojas sin nada; ya cambiamos el tóner y sigue igual.")),
        ("diego", new("El VPN se corta cada 10 minutos", "Trabajo desde casa y la conexión se cae seguido; vuelve sola después de un rato.", TicketPriority.High)),
        ("diego", new("Error al exportar un reporte a Excel", "El reporte de ventas mensual muestra un error al exportarlo; en PDF funciona bien."))
    ];

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

        await SeedRolesAsync(provider.GetRequiredService<RoleManager<IdentityRole>>());

        string? password = provider.GetRequiredService<IConfiguration>()[PasswordKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevelopmentSeeder))
                .LogWarning("Sin usuarios de desarrollo: falta {Key} en User Secrets.", PasswordKey);
            return;
        }

        await SeedUsersAsync(provider.GetRequiredService<UserManager<IdentityUser>>(), password);

        if (!await db.Tickets.AnyAsync(ticket => ticket.RequestedBy != null, cancellationToken))
        {
            await SeedPortalTicketsAsync(provider.GetRequiredService<OpenTicketHandler>(), cancellationToken);
        }
    }

    // Por el mismo caso de uso que el portal: ticket y descripción, como si los hubiera abierto cada cliente.
    private static async Task SeedPortalTicketsAsync(OpenTicketHandler handler, CancellationToken cancellationToken)
    {
        foreach ((string customer, OpenTicketRequest request) in SamplePortalTickets)
        {
            await handler.HandleAsync(request, $"{customer}@{CustomerEmailDomain}", customer, cancellationToken);
        }
    }

    private static async Task SeedRolesAsync(RoleManager<IdentityRole> roles)
    {
        foreach (string role in HelpDeskRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole(role)), $"crear el rol {role}");
            }
        }
    }

    // Los agentes llevan el claim que une su cuenta con Ticket.AssignedTo; el supervisor y el admin, no.
    private static async Task SeedUsersAsync(UserManager<IdentityUser> users, string password)
    {
        foreach (string agent in AgentNames)
        {
            await EnsureUserAsync(users, agent, password, HelpDeskRoles.Agent, new Claim(HelpDeskClaimTypes.Agent, agent));
        }

        await EnsureUserAsync(users, "laura", password, HelpDeskRoles.Supervisor);
        await EnsureUserAsync(users, "admin", password, HelpDeskRoles.Admin);

        foreach (string customer in CustomerNames)
        {
            await EnsureUserAsync(users, customer, password, HelpDeskRoles.Customer, emailDomain: CustomerEmailDomain);
        }
    }

    private static async Task EnsureUserAsync(
        UserManager<IdentityUser> users,
        string name,
        string password,
        string role,
        Claim? claim = null,
        string emailDomain = EmailDomain)
    {
        string email = $"{name}@{emailDomain}";
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        // Con MapIdentityApi el nombre de usuario ES el email: así se inicia sesión.
        IdentityUser user = new() { UserName = email, Email = email, EmailConfirmed = true };

        EnsureSucceeded(await users.CreateAsync(user, password), $"crear el usuario {email}");
        EnsureSucceeded(await users.AddToRoleAsync(user, role), $"asignar el rol {role} a {email}");

        if (claim is not null)
        {
            EnsureSucceeded(await users.AddClaimAsync(user, claim), $"agregar el claim {claim.Type} a {email}");
        }
    }

    // Identity no lanza excepciones: devuelve IdentityResult. Ignorarlo dejaría usuarios a medio crear.
    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            string errors = string.Join(" ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"No se pudo {action}: {errors}");
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
