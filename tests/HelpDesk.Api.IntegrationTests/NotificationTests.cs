using System.Net;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Domain;

namespace HelpDesk.Api.IntegrationTests;

// Para probar que alguien NO recibió un aviso no se espera un tiempo fijo: se manda después otro que esa persona sí debe
// recibir. Cuando ese llega, el anterior ya habría llegado.
public sealed class NotificationTests(HelpDeskApiFactory api)
{
    private const string Hub = "/hubs/tickets/negotiate?negotiateVersion=1";

    [Fact]
    public async Task Hub_RejectsAnonymousConnections() =>
        (await api.Anonymous().PostAsync(Hub)).ShouldBe(HttpStatusCode.Unauthorized);

    // Cross-Site WebSocket Hijacking: la cookie sirve para la API, y no debe abrir el hub.
    [Fact]
    public async Task Hub_RejectsTheIdentityCookie()
    {
        var browser = new ApiUser(api.CreateClient());
        (await browser.PostAsync("/api/auth/login?useCookies=true", api.CredentialsOf("laura"))).ShouldBe(HttpStatusCode.OK);

        (await browser.GetAsync("/api/me")).ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync(Hub)).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NewTicket_ReachesItsAgentAndSupervision_AndNobodyElse()
    {
        await using HubListener laura = await HubListener.ConnectAsync(api, "laura");
        await using HubListener pedro = await HubListener.ConnectAsync(api, "pedro");
        await using HubListener luis = await HubListener.ConnectAsync(api, "luis");
        ApiUser supervisor = await api.AsAsync("laura");

        TicketResponse forPedro = await supervisor.CreateTicketAsync(Test.UniqueTitle("De pedro"), assignedTo: "pedro");
        TicketResponse forLuis = await supervisor.CreateTicketAsync(Test.UniqueTitle("De luis"), assignedTo: "luis");

        TicketEvent received = await pedro.WaitForAsync<TicketEvent>(ticket => ticket.TicketId == forPedro.Id);
        Assert.Equal(TicketEventKind.Created, received.Kind);
        Assert.Equal("laura", received.Actor);
        await laura.WaitForAsync<TicketEvent>(ticket => ticket.TicketId == forPedro.Id);

        await luis.WaitForAsync<TicketEvent>(ticket => ticket.TicketId == forLuis.Id);
        Assert.DoesNotContain(luis.Received<TicketEvent>(), ticket => ticket.TicketId == forPedro.Id);
    }

    [Fact]
    public async Task AgentNames_DoNotDependOnCase()
    {
        await using HubListener luis = await HubListener.ConnectAsync(api, "luis");
        ApiUser supervisor = await api.AsAsync("laura");

        TicketResponse ticket = await supervisor.CreateTicketAsync(Test.UniqueTitle("En mayúsculas"), assignedTo: "LUIS");

        await luis.WaitForAsync<TicketEvent>(received => received.TicketId == ticket.Id);
    }

    // EF escribe los valores nuevos sobre el ticket al guardar: por eso el agente anterior se calcula antes.
    [Fact]
    public async Task Reassigning_AlsoReachesWhoHadTheTicket()
    {
        await using HubListener pedro = await HubListener.ConnectAsync(api, "pedro");
        await using HubListener luis = await HubListener.ConnectAsync(api, "luis");
        ApiUser supervisor = await api.AsAsync("laura");
        TicketResponse ticket = await supervisor.CreateTicketAsync(Test.UniqueTitle("Cambia de manos"), assignedTo: "pedro");

        await (await supervisor.UpdateTicketAsync(ticket.Id, assignedTo: "luis")).ReadAsync<TicketResponse>();

        Func<TicketEvent, bool> isReassignment = received => received.TicketId == ticket.Id && received.Kind == TicketEventKind.Reassigned;
        await pedro.WaitForAsync(isReassignment);
        await luis.WaitForAsync(isReassignment);
    }

    [Fact]
    public async Task ChangingOnlyTheCaseOfTheAgent_IsNotAReassignment()
    {
        await using HubListener pedro = await HubListener.ConnectAsync(api, "pedro");
        ApiUser supervisor = await api.AsAsync("laura");
        TicketResponse ticket = await supervisor.CreateTicketAsync(Test.UniqueTitle("Mayúsculas"), assignedTo: "pedro");

        await (await supervisor.UpdateTicketAsync(ticket.Id, assignedTo: "PEDRO")).ReadAsync<TicketResponse>();

        TicketEvent received = await pedro.WaitForAsync<TicketEvent>(notice => notice.TicketId == ticket.Id && notice.Kind != TicketEventKind.Created);
        Assert.Equal(TicketEventKind.Updated, received.Kind);
    }

    [Fact]
    public async Task Customer_ReceivesTheirOwnContract_WithoutInternalData()
    {
        await using HubListener camila = await HubListener.ConnectAsync(api, "camila");
        await using HubListener diego = await HubListener.ConnectAsync(api, "diego");
        ApiUser customer = await api.AsAsync("camila");
        ApiUser otherCustomer = await api.AsAsync("diego");
        ApiUser supervisor = await api.AsAsync("laura");

        PortalTicketResponse ticket = await customer.OpenTicketAsync(Test.UniqueTitle("De camila"));
        PortalTicketEvent opened = await camila.WaitForAsync<PortalTicketEvent>(received => received.TicketId == ticket.Id);
        Assert.False(opened.BySupport);

        await (await supervisor.PostAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("Respuesta"))).ReadAsync<CommentResponse>();
        PortalTicketEvent reply = await camila.WaitForAsync<PortalTicketEvent>(received => received.TicketId == ticket.Id && received.Kind == TicketEventKind.Commented);
        Assert.True(reply.BySupport);

        PortalTicketResponse theirs = await otherCustomer.OpenTicketAsync(Test.UniqueTitle("De diego"));
        await diego.WaitForAsync<PortalTicketEvent>(received => received.TicketId == theirs.Id);
        Assert.DoesNotContain(diego.Received<PortalTicketEvent>(), received => received.TicketId == ticket.Id);
    }

    [Fact]
    public async Task ChangingOnlyTheAgent_IsNotReportedToTheCustomer()
    {
        await using HubListener camila = await HubListener.ConnectAsync(api, "camila");
        ApiUser customer = await api.AsAsync("camila");
        ApiUser supervisor = await api.AsAsync("laura");
        PortalTicketResponse ticket = await customer.OpenTicketAsync(Test.UniqueTitle("Se asigna"));

        await (await supervisor.UpdateTicketAsync(ticket.Id, assignedTo: "pedro")).ReadAsync<TicketResponse>();
        await (await supervisor.UpdateTicketAsync(ticket.Id, status: TicketStatus.InProgress)).ReadAsync<TicketResponse>();

        await camila.WaitForAsync<PortalTicketEvent>(received => received.TicketId == ticket.Id && received.Status == TicketStatus.InProgress);
        Assert.DoesNotContain(camila.Received<PortalTicketEvent>(), received => received.TicketId == ticket.Id && received.Kind == TicketEventKind.Reassigned);
    }

    [Fact]
    public async Task WhatWasNotSaved_IsNotReported()
    {
        await using HubListener luis = await HubListener.ConnectAsync(api, "luis");
        ApiUser supervisor = await api.AsAsync("laura");
        TicketResponse ticket = await supervisor.CreateTicketAsync(Test.UniqueTitle("Conflicto"), assignedTo: "luis");
        await luis.WaitForAsync<TicketEvent>(received => received.TicketId == ticket.Id);
        int before = luis.Received<TicketEvent>().Count(received => received.TicketId == ticket.Id);

        (await supervisor.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Tarde"), version: "12345")).ShouldBe(HttpStatusCode.Conflict);

        TicketResponse next = await supervisor.CreateTicketAsync(Test.UniqueTitle("El siguiente"), assignedTo: "luis");
        await luis.WaitForAsync<TicketEvent>(received => received.TicketId == next.Id);
        Assert.Equal(before, luis.Received<TicketEvent>().Count(received => received.TicketId == ticket.Id));
    }

    [Fact]
    public async Task BulkReassign_IsASingleNotice()
    {
        await using HubListener pedro = await HubListener.ConnectAsync(api, "pedro");
        ApiUser supervisor = await api.AsAsync("laura");
        string from = Test.UniqueAgent();
        await supervisor.CreateTicketAsync(Test.UniqueTitle("Uno"), assignedTo: from);
        await supervisor.CreateTicketAsync(Test.UniqueTitle("Dos"), assignedTo: from);

        await (await supervisor.PostAsync($"/api/agents/{from}/reassign", new ReassignTicketsRequest("pedro"))).ReadAsync<ReassignTicketsResponse>();

        TicketsReassignedEvent notice = await pedro.WaitForAsync<TicketsReassignedEvent>(received => received.From == from);
        Assert.Equal(2, notice.Tickets);
        Assert.Single(pedro.Received<TicketsReassignedEvent>(), received => received.From == from);
    }
}
