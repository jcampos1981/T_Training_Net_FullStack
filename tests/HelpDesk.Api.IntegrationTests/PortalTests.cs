using System.Net;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;

namespace HelpDesk.Api.IntegrationTests;

public sealed class PortalTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task OpenTicket_KeepsTheDescriptionAsTheFirstComment()
    {
        ApiUser camila = await api.AsAsync("camila");

        PortalTicketResponse ticket = await camila.OpenTicketAsync(Test.UniqueTitle("No imprime"));
        CommentResponse[] conversation = await camila.ReadAsync<CommentResponse[]>($"/api/portal/tickets/{ticket.Id}/comments");

        CommentResponse description = Assert.Single(conversation);
        Assert.Equal("Descripción de la prueba.", description.Body);
        Assert.Equal("camila", description.Author);
    }

    [Fact]
    public async Task List_OnlyHasMyTickets()
    {
        ApiUser camila = await api.AsAsync("camila");
        ApiUser diego = await api.AsAsync("diego");
        PortalTicketResponse mine = await camila.OpenTicketAsync(Test.UniqueTitle("De camila"));
        PortalTicketResponse theirs = await diego.OpenTicketAsync(Test.UniqueTitle("De diego"));

        CursorPage<PortalTicketResponse> page = await camila.ReadAsync<CursorPage<PortalTicketResponse>>("/api/portal/tickets?pageSize=100");

        Assert.Contains(page.Items, ticket => ticket.Id == mine.Id);
        Assert.DoesNotContain(page.Items, ticket => ticket.Id == theirs.Id);
    }

    // 404 y no 403: un 403 confirmaría que ese Id existe.
    [Fact]
    public async Task AnotherCustomersTicket_LooksLikeItDoesNotExist()
    {
        ApiUser camila = await api.AsAsync("camila");
        ApiUser diego = await api.AsAsync("diego");
        PortalTicketResponse theirs = await diego.OpenTicketAsync(Test.UniqueTitle("De diego"));

        (await camila.GetAsync($"/api/portal/tickets/{theirs.Id}")).ShouldBe(HttpStatusCode.NotFound);
        (await camila.GetAsync($"/api/portal/tickets/{theirs.Id}/comments")).ShouldBe(HttpStatusCode.NotFound);
        (await camila.PostAsync($"/api/portal/tickets/{theirs.Id}/comments", new AddCommentRequest("Hola"))).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Contract_DoesNotRevealInternalData()
    {
        ApiUser laura = await api.AsAsync("laura");
        ApiUser camila = await api.AsAsync("camila");
        PortalTicketResponse ticket = await camila.OpenTicketAsync(Test.UniqueTitle("Con agente"));
        await (await laura.UpdateTicketAsync(ticket.Id, assignedTo: "pedro")).ReadAsync<TicketResponse>();

        string json = await (await camila.GetAsync($"/api/portal/tickets/{ticket.Id}")).TextAsync();

        Assert.DoesNotContain("pedro", json);
        Assert.DoesNotContain("assignedTo", json);
        Assert.DoesNotContain("version", json);
        Assert.DoesNotContain("sla", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Customer_CannotMarkATicketAsCritical()
    {
        ApiUser camila = await api.AsAsync("camila");

        HttpResponseMessage response = await camila.PostAsync(
            "/api/portal/tickets", new OpenTicketRequest(Test.UniqueTitle("Urgente"), "Descripción de la prueba.", TicketPriority.Critical));

        response.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ClosedTicket_DoesNotAcceptReplies()
    {
        ApiUser laura = await api.AsAsync("laura");
        ApiUser camila = await api.AsAsync("camila");
        PortalTicketResponse ticket = await camila.OpenTicketAsync(Test.UniqueTitle("Se cierra"));
        await (await laura.UpdateTicketAsync(ticket.Id, status: TicketStatus.Closed)).ReadAsync<TicketResponse>();

        (await camila.PostAsync($"/api/portal/tickets/{ticket.Id}/comments", new AddCommentRequest("Sigue igual"))).ShouldBe(HttpStatusCode.Conflict);
    }
}
