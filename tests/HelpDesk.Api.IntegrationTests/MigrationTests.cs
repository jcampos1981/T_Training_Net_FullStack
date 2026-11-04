using HelpDesk.Api.Data;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests;

public sealed class MigrationTests(HelpDeskApiFactory api)
{
    // Si alguien cambia una entidad y olvida crear la migración, esta prueba lo dice antes de llegar a producción.
    [Fact]
    public async Task Model_HasNoChangesWithoutAMigration()
    {
        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Test.Cancel));
    }
}
