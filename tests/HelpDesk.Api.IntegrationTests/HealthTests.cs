using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace HelpDesk.Api.IntegrationTests;

public sealed class HealthTests(HelpDeskApiFactory api)
{
    // Un puerto donde no hay nadie escuchando: la base está "caída" solo para esta instancia de la API.
    private const string BrokenDatabase = "Server=127.0.0.1,1;Database=HelpDesk;User Id=sa;Password=NoImporta1;TrustServerCertificate=True;Connect Timeout=1";

    [Fact]
    public async Task Live_IsOpen_AndSaysNothingElse()
    {
        HttpResponseMessage response = await api.Anonymous().GetAsync("/health/live");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Healthy", await response.TextAsync());
    }

    [Fact]
    public async Task Ready_IsOpen_AndDoesNotRevealWhatItChecks()
    {
        HttpResponseMessage response = await api.Anonymous().GetAsync("/health/ready");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Healthy", await response.TextAsync());
    }

    [Fact]
    public async Task Details_AreOnlyForSupervision()
    {
        (await api.Anonymous().GetAsync("/health/details")).ShouldBe(HttpStatusCode.Unauthorized);
        (await (await api.AsAsync("pedro")).GetAsync("/health/details")).ShouldBe(HttpStatusCode.Forbidden);

        using JsonDocument details = JsonDocument.Parse(await (await (await api.AsAsync("laura")).GetAsync("/health/details")).TextAsync());

        string[] checks = [.. details.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()!)];
        Assert.Contains("database", checks);
        Assert.Contains("sla-monitor", checks);
    }

    // Sin base, deja de recibir tráfico, y no pide que la reinicien: reiniciar no arreglaría la base.
    [Fact]
    public async Task WithoutDatabase_ReadyFailsFast_AndLiveStaysUp()
    {
        await using var broken = new HelpDeskApiInstance(api.WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:HelpDesk", BrokenDatabase)));
        var anonymous = new ApiUser(broken.Client);

        var timer = Stopwatch.StartNew();
        HttpResponseMessage ready = await anonymous.GetAsync("/health/ready");
        timer.Stop();

        ready.ShouldBe(HttpStatusCode.ServiceUnavailable);
        Assert.Equal("Unhealthy", await ready.TextAsync());
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"Tardó {timer.Elapsed.TotalSeconds:N1} s.");

        (await anonymous.GetAsync("/health/live")).ShouldBe(HttpStatusCode.OK);
    }
}
