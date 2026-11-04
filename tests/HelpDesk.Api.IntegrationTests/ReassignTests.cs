using System.Net;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Domain;

namespace HelpDesk.Api.IntegrationTests;

public sealed class ReassignTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task MovesOnlyOpenTickets_AndLeavesACommentOnEach()
    {
        ApiUser laura = await api.AsAsync("laura");
        string from = Test.UniqueAgent();
        string to = Test.UniqueAgent();
        TicketResponse open = await laura.CreateTicketAsync(Test.UniqueTitle("Abierto"), assignedTo: from);
        TicketResponse closed = await laura.CreateTicketAsync(Test.UniqueTitle("Cerrado"), assignedTo: from);
        await (await laura.UpdateTicketAsync(closed.Id, status: TicketStatus.Closed)).ReadAsync<TicketResponse>();

        ReassignTicketsResponse result = await (await laura.PostAsync($"/api/agents/{from}/reassign", new ReassignTicketsRequest(to))).ReadAsync<ReassignTicketsResponse>();

        Assert.Equal(1, result.ReassignedTickets);
        Assert.Equal(to, (await laura.ReadAsync<TicketResponse>($"/api/tickets/{open.Id}")).AssignedTo);
        Assert.Equal(from, (await laura.ReadAsync<TicketResponse>($"/api/tickets/{closed.Id}")).AssignedTo);

        CommentResponse note = Assert.Single(await laura.ReadAsync<CommentResponse[]>($"/api/tickets/{open.Id}/comments"));
        Assert.Equal("sistema", note.Author);
    }

    [Fact]
    public async Task ToTheSameAgent_IsRejected()
    {
        ApiUser laura = await api.AsAsync("laura");

        (await laura.PostAsync("/api/agents/pedro/reassign", new ReassignTicketsRequest("PEDRO"))).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Agent_CannotReassign()
    {
        ApiUser pedro = await api.AsAsync("pedro");

        (await pedro.PostAsync("/api/agents/luis/reassign", new ReassignTicketsRequest("pedro"))).ShouldBe(HttpStatusCode.Forbidden);
    }
}
