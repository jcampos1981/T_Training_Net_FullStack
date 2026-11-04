using System.Net;
using Dapper;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Sla;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests;

[Collection(SequentialCollection.Name)]
public sealed class SlaTests(HelpDeskApiFactory api)
{
    private const double AtRisk = 0.9;
    private const double Breached = 1.1;

    [Fact]
    public async Task TicketInTime_HasNoAlert()
    {
        Ticket ticket = await InsertCriticalAsync(fractionOfDeadline: 0.5);

        Assert.Empty(OfTicket(await ScanAsync(), ticket));
    }

    [Fact]
    public async Task AtRisk_ThenBreached_EachAlertsOnce()
    {
        Ticket ticket = await InsertCriticalAsync(AtRisk);

        RaisedSlaAlert first = Assert.Single(OfTicket(await ScanAsync(), ticket));
        Assert.Equal(SlaState.AtRisk, first.Level);
        Assert.Empty(OfTicket(await ScanAsync(), ticket));

        await MoveCreationBackAsync(ticket, TimeSpan.FromHours(1));

        RaisedSlaAlert second = Assert.Single(OfTicket(await ScanAsync(), ticket));
        Assert.Equal(SlaState.Breached, second.Level);
        Assert.Empty(OfTicket(await ScanAsync(), ticket));
    }

    [Fact]
    public async Task AlreadyBreached_DoesNotGoThroughAtRisk()
    {
        Ticket ticket = await InsertCriticalAsync(Breached);

        RaisedSlaAlert alert = Assert.Single(OfTicket(await ScanAsync(), ticket));

        Assert.Equal(SlaState.Breached, alert.Level);
    }

    [Fact]
    public async Task ResolvedTicket_HasNoAlert()
    {
        Ticket ticket = await InsertCriticalAsync(Breached);
        ApiUser laura = await api.AsAsync("laura");
        await (await laura.UpdateTicketAsync(ticket.Id.Value, status: TicketStatus.Resolved)).ReadAsync<TicketResponse>();

        Assert.Empty(OfTicket(await ScanAsync(), ticket));
    }

    // Por esto SlaAlerts es otra tabla: una columna en Tickets cambiaría el rowversion, con un 409 falso a quien edita.
    [Fact]
    public async Task Alert_DoesNotChangeTheTicketVersion()
    {
        Ticket ticket = await InsertCriticalAsync(AtRisk, assignedTo: "pedro");
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse before = await laura.ReadAsync<TicketResponse>($"/api/tickets/{ticket.Id.Value}");

        Assert.Single(OfTicket(await ScanAsync(), ticket));

        (await laura.UpdateTicketAsync(ticket.Id.Value, title: Test.UniqueTitle("Sin conflicto"), version: before.Version)).ShouldBe(HttpStatusCode.OK);
    }

    // Dos instancias de la API compitiendo. Más que los lotes, para que los dos barridos se crucen.
    [Fact]
    public async Task TwoScansAtOnce_NeverRaiseTheSameAlertTwice()
    {
        Ticket[] tickets = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => InsertCriticalAsync(Breached)));

        IReadOnlyList<RaisedSlaAlert>[] scans = await Task.WhenAll(ScanAsync(), ScanAsync(), ScanAsync());

        int[] ids = [.. tickets.Select(ticket => ticket.Id.Value)];
        IEnumerable<int> raised = scans.SelectMany(scan => scan).Select(alert => alert.TicketId).Where(ids.Contains);

        Assert.Equal(ids.Order(), raised.Order());
    }

    // EF no sabe expresar esta opción: la crea la migración con SQL a mano, y otra migración podría perderla sin avisar.
    [Fact]
    public async Task UniqueIndex_KeepsIgnoreDupKey()
    {
        await using var connection = new SqlConnection(api.ConnectionString);

        bool ignoresDuplicates = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT ignore_dup_key FROM sys.indexes WHERE name = N'IX_SlaAlerts_TicketId_Level'",
            cancellationToken: Test.Cancel));

        Assert.True(ignoresDuplicates);
    }

    [Fact]
    public async Task DeletingATicket_DeletesItsAlerts()
    {
        Ticket ticket = await InsertCriticalAsync(Breached);
        Assert.Single(OfTicket(await ScanAsync(), ticket));

        (await (await api.AsAsync("laura")).DeleteAsync($"/api/tickets/{ticket.Id.Value}")).ShouldBe(HttpStatusCode.NoContent);

        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();
        Assert.False(await db.SlaAlerts.AnyAsync(alert => alert.TicketId == ticket.Id, Test.Cancel));
    }

    [Fact]
    public async Task List_ShowsTheMostSevereOnce_NewestFirst_AndOnlyMine()
    {
        Ticket risky = await InsertCriticalAsync(AtRisk, assignedTo: "pedro");
        await ScanAsync();
        await MoveCreationBackAsync(risky, TimeSpan.FromHours(1));
        await ScanAsync();
        Ticket newest = await InsertCriticalAsync(AtRisk, assignedTo: "pedro");
        await ScanAsync();

        ApiUser pedro = await api.AsAsync("pedro");
        CursorPage<SlaAlertResponse> page = await pedro.ReadAsync<CursorPage<SlaAlertResponse>>("/api/sla/alerts?pageSize=100");

        Assert.Equal(newest.Id.Value, page.Items[0].TicketId);
        Assert.All(page.Items, alert => Assert.Equal("pedro", alert.AssignedTo));
        Assert.Equal(SlaState.Breached, Assert.Single(page.Items, alert => alert.TicketId == risky.Id.Value).Level);
        Assert.Equal(page.Items.Select(alert => alert.Id).OrderDescending(), page.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task List_FiltersByLevel_AndDoesNotIncludeResolvedTickets()
    {
        Ticket risky = await InsertCriticalAsync(AtRisk, assignedTo: "luis");
        Ticket resolved = await InsertCriticalAsync(AtRisk, assignedTo: "luis");
        await ScanAsync();
        ApiUser laura = await api.AsAsync("laura");
        await (await laura.UpdateTicketAsync(resolved.Id.Value, status: TicketStatus.Resolved)).ReadAsync<TicketResponse>();

        CursorPage<SlaAlertResponse> page = await laura.ReadAsync<CursorPage<SlaAlertResponse>>("/api/sla/alerts?level=AtRisk&pageSize=100");

        Assert.Contains(page.Items, alert => alert.TicketId == risky.Id.Value);
        Assert.DoesNotContain(page.Items, alert => alert.TicketId == resolved.Id.Value);
        Assert.All(page.Items, alert => Assert.Equal(SlaState.AtRisk, alert.Level));
    }

    [Fact]
    public async Task List_IsOnlyForTheTeam()
    {
        (await api.Anonymous().GetAsync("/api/sla/alerts")).ShouldBe(HttpStatusCode.Unauthorized);
        (await (await api.AsAsync("camila")).GetAsync("/api/sla/alerts")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Notice_GoesToItsAgentAndSupervision_NotToTheCustomer()
    {
        await using HubListener laura = await HubListener.ConnectAsync(api, "laura");
        await using HubListener pedro = await HubListener.ConnectAsync(api, "pedro");
        await using HubListener luis = await HubListener.ConnectAsync(api, "luis");
        await using HubListener camila = await HubListener.ConnectAsync(api, "camila");
        Ticket ticket = await InsertCriticalAsync(AtRisk, assignedTo: "pedro", requestedBy: "camila@acme.local");

        // Solo las de esta prueba: el barrido recoge además las de cualquier otro ticket vencido, como los de ejemplo.
        IReadOnlyList<RaisedSlaAlert> raised = await ScanAsync();
        await api.Services.GetRequiredService<ITicketNotifier>().SlaAlertsRaisedAsync([.. OfTicket(raised, ticket)]);

        SlaAlertsEvent notice = await pedro.WaitForAsync<SlaAlertsEvent>(received => received.Ticket?.Id == ticket.Id.Value);
        Assert.Equal(1, notice.AtRisk);
        await laura.WaitForAsync<SlaAlertsEvent>(received => received.Ticket?.Id == ticket.Id.Value);

        await api.Services.GetRequiredService<ITicketNotifier>().SlaAlertsRaisedAsync(
            [new RaisedSlaAlert(ticket.Id.Value, SlaState.Breached, ticket.Title, "luis")]);
        await luis.WaitForAsync<SlaAlertsEvent>(received => received.Breached == 1);

        Assert.DoesNotContain(luis.Received<SlaAlertsEvent>(), received => received.AtRisk > 0);
        Assert.Empty(camila.Received<SlaAlertsEvent>());
    }

    [Fact]
    public async Task ManyAlerts_AreOneNoticeWithTheTotal()
    {
        string agent = "marta";
        await using HubListener marta = await HubListener.ConnectAsync(api, agent);
        Ticket[] tickets = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => InsertCriticalAsync(AtRisk, assignedTo: agent)));

        IReadOnlyList<RaisedSlaAlert> raised = await ScanAsync();
        await api.Services.GetRequiredService<ITicketNotifier>().SlaAlertsRaisedAsync(
            [.. tickets.SelectMany(ticket => OfTicket(raised, ticket))]);

        SlaAlertsEvent notice = await marta.WaitForAsync<SlaAlertsEvent>(received => received.AtRisk == tickets.Length);
        Assert.Null(notice.Ticket);
        Assert.Single(marta.Received<SlaAlertsEvent>());
    }

    private async Task<IReadOnlyList<RaisedSlaAlert>> ScanAsync()
    {
        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<SlaAlertScanner>().ScanAsync(Test.Cancel);
    }

    private Task<Ticket> InsertCriticalAsync(double fractionOfDeadline, string? assignedTo = null, string? requestedBy = null) =>
        api.InsertTicketAsync(Test.UniqueTitle("SLA"), TicketPriority.Critical, TicketTimes.CriticalAt(fractionOfDeadline), assignedTo, requestedBy);

    private async Task MoveCreationBackAsync(Ticket ticket, TimeSpan howMuch)
    {
        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        HelpDeskDbContext db = scope.ServiceProvider.GetRequiredService<HelpDeskDbContext>();

        await db.Tickets
            .Where(row => row.Id == ticket.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedAt, ticket.CreatedAt - howMuch), Test.Cancel);
    }

    private static IEnumerable<RaisedSlaAlert> OfTicket(IEnumerable<RaisedSlaAlert> alerts, Ticket ticket) =>
        alerts.Where(alert => alert.TicketId == ticket.Id.Value);
}
