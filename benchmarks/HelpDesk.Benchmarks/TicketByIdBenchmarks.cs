using BenchmarkDotNet.Attributes;
using HelpDesk.Api.Data;
using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace HelpDesk.Benchmarks;

// GET /api/tickets/{id}: la consulta más simple y más frecuente de la API. ¿Cuánto ahorran las dos
// optimizaciones de EF Core que se suelen recomendar? Cada operación usa su propio DbContext, como cada petición
// HTTP, contra la base de desarrollo de verdad: el viaje a SQL Server forma parte de lo que se mide.
[MemoryDiagnoser] // además del tiempo, cuánta memoria se reserva por operación
[SimpleJob(launchCount: 1, warmupCount: 5, iterationCount: 15)]
public class TicketByIdBenchmarks
{
    // EF.CompileAsyncQuery traduce el LINQ una sola vez, al crear el delegado. Sin compilar, EF también guarda
    // la traducción en su caché, pero en cada llamada recorre el árbol de expresión para encontrarla.
    private static readonly Func<HelpDeskDbContext, TicketId, Task<Ticket?>> FindCompiled =
        EF.CompileAsyncQuery((HelpDeskDbContext db, TicketId id) =>
            db.Tickets.AsNoTracking().FirstOrDefault(ticket => ticket.Id == id));

    private readonly TicketId ticketId = new(822);
    private DbContextOptions<HelpDeskDbContext> options = null!;
    private PooledDbContextFactory<HelpDeskDbContext> pool = null!;

    [GlobalSetup]
    public void Setup()
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__HelpDesk")
            ?? throw new InvalidOperationException("Falta ConnectionStrings__HelpDesk: corre benchmarks/Run-Benchmarks.ps1.");

        options = new DbContextOptionsBuilder<HelpDeskDbContext>().UseSqlServer(connectionString).Options;

        // Un pool reutiliza instancias de DbContext ya construidas (lo mismo que AddDbContextPool en la API).
        pool = new PooledDbContextFactory<HelpDeskDbContext>(options);
    }

    [Benchmark(Baseline = true, Description = "LINQ (como hoy)")]
    public async Task<Ticket?> Linq()
    {
        await using HelpDeskDbContext db = new(options);
        return await db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
    }

    [Benchmark(Description = "Consulta compilada")]
    public async Task<Ticket?> Compiled()
    {
        await using HelpDeskDbContext db = new(options);
        return await FindCompiled(db, ticketId);
    }

    [Benchmark(Description = "LINQ + pool de DbContext")]
    public async Task<Ticket?> LinqPooled()
    {
        await using HelpDeskDbContext db = pool.CreateDbContext();
        return await db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket => ticket.Id == ticketId);
    }

    [Benchmark(Description = "Compilada + pool")]
    public async Task<Ticket?> CompiledPooled()
    {
        await using HelpDeskDbContext db = pool.CreateDbContext();
        return await FindCompiled(db, ticketId);
    }
}
