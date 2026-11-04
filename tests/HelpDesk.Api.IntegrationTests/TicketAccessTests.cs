using System.Net;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Paging;

namespace HelpDesk.Api.IntegrationTests;

public sealed class TicketAccessTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task WithoutCredentials_IsRejected() =>
        (await api.Anonymous().GetAsync("/api/tickets")).ShouldBe(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Agent_ListsOnlyOwnTickets()
    {
        ApiUser laura = await api.AsAsync("laura");
        await laura.CreateTicketAsync(Test.UniqueTitle("De pedro"), assignedTo: "pedro");
        await laura.CreateTicketAsync(Test.UniqueTitle("De luis"), assignedTo: "luis");

        ApiUser pedro = await api.AsAsync("pedro");
        CursorPage<TicketResponse> page = await pedro.ReadAsync<CursorPage<TicketResponse>>("/api/tickets?pageSize=100");

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, ticket => Assert.Equal("pedro", ticket.AssignedTo));
    }

    [Fact]
    public async Task Agent_CannotListAnotherAgentsTickets()
    {
        ApiUser pedro = await api.AsAsync("pedro");

        (await pedro.GetAsync("/api/tickets?assignedTo=luis")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Agent_CannotReadAnotherAgentsTicket_ButSupervisorCan()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("De luis"), assignedTo: "luis");

        ApiUser pedro = await api.AsAsync("pedro");

        (await pedro.GetAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.Forbidden);
        (await laura.GetAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Agent_CannotReassignOwnTicket()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("De pedro"), assignedTo: "pedro");

        ApiUser pedro = await api.AsAsync("pedro");

        (await pedro.UpdateTicketAsync(ticket.Id, assignedTo: "luis")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Agent_CannotCreateTicketForAnotherAgent()
    {
        ApiUser pedro = await api.AsAsync("pedro");

        HttpResponseMessage response = await pedro.PostAsync(
            "/api/tickets", new CreateTicketRequest(Test.UniqueTitle("Para luis"), Domain.TicketPriority.Low, "luis"));

        response.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Agent_CannotDeleteTickets()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("De pedro"), assignedTo: "pedro");

        ApiUser pedro = await api.AsAsync("pedro");

        (await pedro.DeleteAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.Forbidden);
        (await laura.DeleteAsync($"/api/tickets/{ticket.Id}")).ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Customer_CannotEnterTheTeamApi()
    {
        ApiUser camila = await api.AsAsync("camila");

        (await camila.GetAsync("/api/tickets")).ShouldBe(HttpStatusCode.Forbidden);
        (await camila.GetAsync("/api/dashboard")).ShouldBe(HttpStatusCode.Forbidden);
    }
}
