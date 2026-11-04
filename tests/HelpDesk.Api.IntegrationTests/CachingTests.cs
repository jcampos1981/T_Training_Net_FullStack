using System.Net;
using Dapper;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Dashboard;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests;

public sealed class CachingTests(HelpDeskApiFactory api)
{
    // Un parámetro distinto en cada prueba es otra entrada de la caché: las pruebas no se pisan.
    private static string AgentsUrl() => $"/api/agents?prueba={Guid.NewGuid():N}";

    [Fact]
    public async Task Agents_SecondRequestComesFromTheCache()
    {
        ApiUser laura = await api.AsAsync("laura");
        string url = AgentsUrl();

        HttpResponseMessage first = await laura.GetAsync(url);
        HttpResponseMessage second = await laura.GetAsync(url);

        Assert.Null(first.Headers.Age);
        Assert.NotNull(second.Headers.Age);
        Assert.Equal(await first.TextAsync(), await second.TextAsync());
    }

    // UseOutputCache va después de UseAuthorization. Al revés, lo guardado le llegaría a cualquiera.
    [Fact]
    public async Task Agents_FullCache_IsNotDeliveredToWhoCannotSeeIt()
    {
        ApiUser laura = await api.AsAsync("laura");
        string url = AgentsUrl();
        (await laura.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull((await laura.GetAsync(url)).Headers.Age);

        (await (await api.AsAsync("pedro")).GetAsync(url)).ShouldBe(HttpStatusCode.Forbidden);
        (await (await api.AsAsync("camila")).GetAsync(url)).ShouldBe(HttpStatusCode.Forbidden);
        (await api.Anonymous().GetAsync(url)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agents_AnErrorIsNotCached()
    {
        string url = AgentsUrl();

        (await (await api.AsAsync("pedro")).GetAsync(url)).ShouldBe(HttpStatusCode.Forbidden);

        HttpResponseMessage allowed = await (await api.AsAsync("laura")).GetAsync(url);
        allowed.ShouldBe(HttpStatusCode.OK);
        Assert.Null(allowed.Headers.Age);
    }

    [Fact]
    public async Task Agents_ReassigningEmptiesTheCache()
    {
        ApiUser laura = await api.AsAsync("laura");
        string from = Test.UniqueAgent();
        string url = AgentsUrl();
        await laura.CreateTicketAsync(Test.UniqueTitle("Se mueve"), assignedTo: from);

        AgentSummaryResponse[] before = await laura.ReadAsync<AgentSummaryResponse[]>(url);
        Assert.Contains(before, agent => agent.Name == from);
        Assert.NotNull((await laura.GetAsync(url)).Headers.Age);

        await (await laura.PostAsync($"/api/agents/{from}/reassign", new ReassignTicketsRequest("pedro"))).ReadAsync<ReassignTicketsResponse>();

        HttpResponseMessage after = await laura.GetAsync(url);
        Assert.Null(after.Headers.Age);
        Assert.DoesNotContain(await after.ReadAsync<AgentSummaryResponse[]>(), agent => agent.Name == from);
    }

    // Se mira en SQL Server cuántas veces se ejecutó de verdad, no lo que dice la API.
    [Fact]
    public async Task Dashboard_ManyRequestsAtOnce_ExecuteTheQueryOnce()
    {
        ApiUser laura = await api.AsAsync("laura");
        await api.Services.GetRequiredService<HybridCache>().RemoveAsync(DashboardFeature.CacheKey, Test.Cancel);
        long before = await DashboardExecutionsAsync();

        DashboardResponse[] dashboards = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => laura.ReadAsync<DashboardResponse>("/api/dashboard")));

        Assert.Equal(1, await DashboardExecutionsAsync() - before);
        Assert.Single(dashboards.Select(dashboard => dashboard.GeneratedAt).Distinct());
        Assert.NotEmpty(dashboards[0].TicketsByStatus);
    }

    [Fact]
    public async Task Dashboard_FullCache_IsNotDeliveredToWhoCannotSeeIt()
    {
        ApiUser laura = await api.AsAsync("laura");
        (await laura.GetAsync("/api/dashboard")).ShouldBe(HttpStatusCode.OK);

        (await (await api.AsAsync("pedro")).GetAsync("/api/dashboard")).ShouldBe(HttpStatusCode.Forbidden);
        (await api.Anonymous().GetAsync("/api/dashboard")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<long> DashboardExecutionsAsync()
    {
        await using var connection = new SqlConnection(api.ConnectionString);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT ISNULL(MAX(qs.execution_count), 0)
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
            WHERE t.text LIKE N'%Dashboard: tickets por estado%' AND t.text NOT LIKE N'%dm_exec_query_stats%'
            """,
            cancellationToken: Test.Cancel));
    }
}
