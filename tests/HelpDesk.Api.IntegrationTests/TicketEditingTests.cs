using System.Net;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.IntegrationTests.Support;
using HelpDesk.Api.Paging;
using HelpDesk.Domain;

namespace HelpDesk.Api.IntegrationTests;

public sealed class TicketEditingTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task Update_WithCurrentVersion_SavesAndChangesTheVersion()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse created = await laura.CreateTicketAsync(Test.UniqueTitle("Original"));

        string newTitle = Test.UniqueTitle("Editado");
        TicketResponse saved = await (await laura.UpdateTicketAsync(created.Id, title: newTitle)).ReadAsync<TicketResponse>();

        Assert.Equal(newTitle, saved.Title);
        Assert.NotEqual(created.Version, saved.Version);
    }

    // rowversion: la concurrencia optimista depende de SQL Server. Un repositorio en memoria no probaría esto.
    [Fact]
    public async Task Update_WithStaleVersion_IsConflict_AndKeepsWhatTheOtherPersonSaved()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse created = await laura.CreateTicketAsync(Test.UniqueTitle("Original"));

        string firstTitle = Test.UniqueTitle("Guardó primero");
        await (await laura.UpdateTicketAsync(created.Id, title: firstTitle)).ReadAsync<TicketResponse>();

        HttpResponseMessage late = await laura.UpdateTicketAsync(created.Id, title: Test.UniqueTitle("Llegó tarde"), version: created.Version);

        late.ShouldBe(HttpStatusCode.Conflict);
        Assert.Equal(firstTitle, (await laura.ReadAsync<TicketResponse>($"/api/tickets/{created.Id}")).Title);
    }

    [Fact]
    public async Task ClosedTicket_CannotBeEditedOrCommented()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Se cierra"));
        await (await laura.UpdateTicketAsync(ticket.Id, status: TicketStatus.Closed)).ReadAsync<TicketResponse>();

        (await laura.UpdateTicketAsync(ticket.Id, title: Test.UniqueTitle("Otro título"))).ShouldBe(HttpStatusCode.Conflict);
        (await laura.PostAsync($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest("Tarde"))).ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task InvalidTitle_ReturnsTheErrorUnderTheJsonFieldName()
    {
        ApiUser laura = await api.AsAsync("laura");

        HttpResponseMessage response = await laura.PostAsync("/api/tickets", new CreateTicketRequest("abc", TicketPriority.Low));

        response.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("\"title\":", await response.TextAsync());
    }

    [Fact]
    public async Task Comment_IsSignedByTheUser_NotByWhatTheRequestSays()
    {
        ApiUser laura = await api.AsAsync("laura");
        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Con comentario"), assignedTo: "pedro");

        ApiUser pedro = await api.AsAsync("pedro");
        CommentResponse comment = await pedro.CommentAsync(ticket.Id);

        Assert.Equal("pedro", comment.Author);
    }

    [Fact]
    public async Task Pages_DoNotRepeatTickets_AndCoverEverything()
    {
        ApiUser laura = await api.AsAsync("laura");
        string agent = Test.UniqueAgent();
        int[] created = [.. await Task.WhenAll(Enumerable.Range(0, 5).Select(async index =>
            (await laura.CreateTicketAsync(Test.UniqueTitle($"Página {index}"), assignedTo: agent)).Id))];

        CursorPage<TicketResponse> first = await laura.ReadAsync<CursorPage<TicketResponse>>($"/api/tickets?assignedTo={agent}&pageSize=2");
        CursorPage<TicketResponse> second = await laura.ReadAsync<CursorPage<TicketResponse>>($"/api/tickets?assignedTo={agent}&pageSize=2&after={first.NextAfter}");
        CursorPage<TicketResponse> third = await laura.ReadAsync<CursorPage<TicketResponse>>($"/api/tickets?assignedTo={agent}&pageSize=2&after={second.NextAfter}");

        IEnumerable<int> seen = first.Items.Concat(second.Items).Concat(third.Items).Select(ticket => ticket.Id);

        Assert.Equal(created.Order(), seen);
        Assert.Null(third.NextAfter);
    }

    [Fact]
    public async Task Response_CarriesTheSlaFromTheDomain()
    {
        ApiUser laura = await api.AsAsync("laura");

        TicketResponse ticket = await laura.CreateTicketAsync(Test.UniqueTitle("Crítico"), TicketPriority.Critical);

        Assert.Equal(SlaState.OnTrack, ticket.SlaState);
        Assert.Equal(ticket.CreatedAt.AddHours(4), ticket.SlaDeadline);
    }
}
