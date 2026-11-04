using Microsoft.AspNetCore.Mvc.Testing;

namespace HelpDesk.Api.IntegrationTests.Support;

// Otra API con una configuración distinta (WithWebHostBuilder), sobre el mismo SQL Server. Se libera al terminar la prueba.
public sealed class HelpDeskApiInstance(WebApplicationFactory<Program> factory) : IAsyncDisposable
{
    public HttpClient Client { get; } = factory.CreateClient();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync();
    }
}
