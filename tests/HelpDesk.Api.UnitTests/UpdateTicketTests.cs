using System.Security.Claims;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;

namespace HelpDesk.Api.UnitTests;

// Mocks con NSubstitute para lo que sale del caso de uso (la base y los avisos). Con ellos se puede probar lo que una prueba de
// integración no ve fácilmente: que algo NO ocurrió, como guardar o avisar después de un rechazo.
public sealed class UpdateTicketTests
{
    private readonly ITicketRepository repository = Substitute.For<ITicketRepository>();
    private readonly ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
    private readonly UpdateTicket.Handler handler;

    public UpdateTicketTests()
    {
        // Guardar devuelve el ticket con los cambios, como el repositorio de verdad.
        repository.UpdateAsync(Arg.Any<Ticket>(), Arg.Any<Ticket>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Result<Ticket>>(call.ArgAt<Ticket>(1)));

        var authorization = Authorization.Real();
        handler = new UpdateTicket.Handler(
            new TicketAccess(repository, authorization), repository, authorization, notifier, Substitute.For<ISlaPolicy>());
    }

    [Fact]
    public async Task TicketThatDoesNotExist_IsNotFound_AndNothingHappens()
    {
        Ticket ticket = Tickets.Assigned("pedro");
        repository.FindAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns((Ticket?)null);

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Supervisor(), Tickets.Edit(ticket));

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        await ShouldNotHaveSavedOrNotified();
    }

    [Fact]
    public async Task AnotherAgentsTicket_IsForbidden_AndNothingHappens()
    {
        Ticket ticket = Found(Tickets.Assigned("luis"));

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Agent("pedro"), Tickets.Edit(ticket, title: "Título nuevo"));

        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        await ShouldNotHaveSavedOrNotified();
    }

    [Fact]
    public async Task Agent_ReassigningOwnTicket_IsForbidden()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Agent("pedro"), Tickets.Edit(ticket, assignedTo: "luis"));

        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        await ShouldNotHaveSavedOrNotified();
    }

    [Fact]
    public async Task Agent_ChangingOnlyTheCaseOfTheirName_IsNotReassigning()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Agent("pedro"), Tickets.Edit(ticket, assignedTo: "PEDRO"));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ClosedTicket_IsAConflict_AndIsNotSaved()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro", TicketStatus.Closed));

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Supervisor(), Tickets.Edit(ticket, title: "Título nuevo"));

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        await ShouldNotHaveSavedOrNotified();
    }

    [Fact]
    public async Task EditConflict_IsReturned_AndNotNotified()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));
        repository.UpdateAsync(Arg.Any<Ticket>(), Arg.Any<Ticket>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Result<Ticket>>(Error.EditConflict));

        Result<TicketResponse> result = await UpdateAsync(ticket, Users.Supervisor(), Tickets.Edit(ticket, title: "Título nuevo"));

        Assert.Same(Error.EditConflict, result.Error);
        await notifier.DidNotReceiveWithAnyArgs().TicketChangedAsync(default!, default, default!);
    }

    [Fact]
    public async Task SavesWithTheVersionTheClientSaw()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));
        UpdateTicketRequest request = Tickets.Edit(ticket, title: "Título nuevo") with { Version = "55" };

        await UpdateAsync(ticket, Users.Supervisor(), request);

        await repository.Received(1).UpdateAsync(ticket, Arg.Any<Ticket>(), 55UL, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingOnlyTheAgent_IsReportedAsReassignment_WithWhoHadIt()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));
        ClaimsPrincipal laura = Users.Supervisor();

        await UpdateAsync(ticket, laura, Tickets.Edit(ticket, assignedTo: "luis"));

        await notifier.Received(1).TicketChangedAsync(
            Arg.Is<Ticket>(saved => saved.AssignedTo == new AgentName("luis")), TicketEventKind.Reassigned, laura, new AgentName("pedro"));
    }

    [Fact]
    public async Task ChangingTheStatusToo_IsReportedAsUpdate()
    {
        Ticket ticket = Found(Tickets.Assigned("pedro"));
        ClaimsPrincipal laura = Users.Supervisor();

        await UpdateAsync(ticket, laura, Tickets.Edit(ticket, status: TicketStatus.InProgress, assignedTo: "luis"));

        await notifier.Received(1).TicketChangedAsync(Arg.Any<Ticket>(), TicketEventKind.Updated, laura, new AgentName("pedro"));
    }

    private Ticket Found(Ticket ticket)
    {
        repository.FindAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        return ticket;
    }

    private Task<Result<TicketResponse>> UpdateAsync(Ticket ticket, ClaimsPrincipal user, UpdateTicketRequest request) =>
        handler.HandleAsync(new UpdateTicket.Command(ticket.Id, request, user), Test.Cancel);

    private async Task ShouldNotHaveSavedOrNotified()
    {
        await repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default, default);
        await notifier.DidNotReceiveWithAnyArgs().TicketChangedAsync(default!, default, default!);
    }
}
