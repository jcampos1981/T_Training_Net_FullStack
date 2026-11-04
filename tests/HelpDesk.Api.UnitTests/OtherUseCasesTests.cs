using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Notifications;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;
using Microsoft.Extensions.Time.Testing;

namespace HelpDesk.Api.UnitTests;

public sealed class CreateTicketTests
{
    private readonly ITicketRepository repository = Substitute.For<ITicketRepository>();
    private readonly ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
    private readonly CreateTicket.Handler handler;

    public CreateTicketTests()
    {
        repository.NextIdAsync(Arg.Any<CancellationToken>()).Returns(new TicketId(9));
        handler = new CreateTicket.Handler(repository, Authorization.Real(), notifier, Substitute.For<ISlaPolicy>(), new FakeTimeProvider(Tickets.Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pedro")]
    [InlineData("PEDRO")]
    public async Task Agent_CanCreateUnassignedOrForThemselves(string? assignedTo)
    {
        var request = new CreateTicketRequest("Título válido", TicketPriority.Low, assignedTo);

        Result<TicketResponse> result = await handler.HandleAsync(new CreateTicket.Command(request, Users.Agent("pedro")), Test.Cancel);

        Assert.True(result.IsSuccess);
        await repository.Received(1).AddAsync(Arg.Any<Ticket>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Agent_CannotCreateForAnotherAgent_AndNothingHappens()
    {
        var request = new CreateTicketRequest("Título válido", TicketPriority.Low, "luis");

        Result<TicketResponse> result = await handler.HandleAsync(new CreateTicket.Command(request, Users.Agent("pedro")), Test.Cancel);

        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        await repository.DidNotReceiveWithAnyArgs().AddAsync(default!, Test.Cancel);
        await notifier.DidNotReceiveWithAnyArgs().TicketChangedAsync(default!, default, default!);
    }

    [Fact]
    public async Task Supervisor_CanCreateForAnyone()
    {
        var request = new CreateTicketRequest("Título válido", TicketPriority.Low, "luis");

        Result<TicketResponse> result = await handler.HandleAsync(new CreateTicket.Command(request, Users.Supervisor()), Test.Cancel);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TakesTheDateFromTheClock_NotFromTheRequest()
    {
        var request = new CreateTicketRequest("Título válido", TicketPriority.Low);

        Result<TicketResponse> result = await handler.HandleAsync(new CreateTicket.Command(request, Users.Supervisor()), Test.Cancel);

        Assert.Equal(Tickets.Now, result.Value.CreatedAt);
    }
}

public sealed class AddCommentTests
{
    private readonly ITicketRepository tickets = Substitute.For<ITicketRepository>();
    private readonly ICommentRepository comments = Substitute.For<ICommentRepository>();
    private readonly ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
    private readonly AddComment.Handler handler;

    public AddCommentTests()
    {
        comments.AddAsync(Arg.Any<TicketComment>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<TicketComment>());
        handler = new AddComment.Handler(new TicketAccess(tickets, Authorization.Real()), comments, notifier, new FakeTimeProvider(Tickets.Now));
    }

    [Fact]
    public async Task IsSignedByTheUser_AndNotified()
    {
        Ticket ticket = Tickets.Assigned("pedro");
        tickets.FindAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        Result<CommentResponse> result = await handler.HandleAsync(
            new AddComment.Command(ticket.Id, new AddCommentRequest("Ya lo revisé."), Users.Agent("pedro")), Test.Cancel);

        Assert.Equal("pedro", result.Value.Author);
        await notifier.Received(1).TicketChangedAsync(ticket, TicketEventKind.Commented, Arg.Any<System.Security.Claims.ClaimsPrincipal>());
    }

    [Fact]
    public async Task OnAClosedTicket_IsAConflict_AndIsNotSaved()
    {
        Ticket ticket = Tickets.Assigned("pedro", TicketStatus.Closed);
        tickets.FindAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);

        Result<CommentResponse> result = await handler.HandleAsync(
            new AddComment.Command(ticket.Id, new AddCommentRequest("Tarde."), Users.Agent("pedro")), Test.Cancel);

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        await comments.DidNotReceiveWithAnyArgs().AddAsync(default!, Test.Cancel);
        await notifier.DidNotReceiveWithAnyArgs().TicketChangedAsync(default!, default, default!);
    }
}

public sealed class DeleteTicketTests
{
    private readonly ITicketRepository repository = Substitute.For<ITicketRepository>();
    private readonly ITicketNotifier notifier = Substitute.For<ITicketNotifier>();

    [Fact]
    public async Task ThatDoesNotExist_IsNotFound()
    {
        var handler = new DeleteTicket.Handler(repository, notifier);

        Result<Unit> result = await handler.HandleAsync(new DeleteTicket.Command(new TicketId(7), Users.Supervisor()), Test.Cancel);

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        await repository.DidNotReceiveWithAnyArgs().DeleteAsync(default, Test.Cancel);
    }

    // Alguien lo borró entre la lectura y el borrado: se responde igual que si no existiera, y no se avisa dos veces.
    [Fact]
    public async Task DeletedByAnotherInTheMeantime_IsNotFound_AndNotNotified()
    {
        Ticket ticket = Tickets.Assigned("pedro");
        repository.FindAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        repository.DeleteAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(false);
        var handler = new DeleteTicket.Handler(repository, notifier);

        Result<Unit> result = await handler.HandleAsync(new DeleteTicket.Command(ticket.Id, Users.Supervisor()), Test.Cancel);

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        await notifier.DidNotReceiveWithAnyArgs().TicketChangedAsync(default!, default, default!);
    }
}

public sealed class TicketAudienceTests
{
    [Fact]
    public void GroupNames_IgnoreCase()
    {
        Assert.Equal(TicketAudience.Agent("pedro"), TicketAudience.Agent("PEDRO"));
        Assert.Equal(TicketAudience.Customer("camila@acme.local"), TicketAudience.Customer("Camila@Acme.Local"));
    }

    [Fact]
    public void Staff_IsSupervisionAndItsAgent()
    {
        Assert.Equal(["supervision", "agent:pedro"], TicketAudience.StaffFor(Tickets.Assigned("pedro"), previousAgent: null));
    }

    [Fact]
    public void Staff_AlsoIncludesWhoHadIt()
    {
        Assert.Equal(["supervision", "agent:luis", "agent:pedro"], TicketAudience.StaffFor(Tickets.Assigned("luis"), new AgentName("pedro")));
    }

    [Fact]
    public void Staff_DoesNotRepeatTheSameAgent_EvenWithDifferentCase()
    {
        Assert.Equal(["supervision", "agent:pedro"], TicketAudience.StaffFor(Tickets.Assigned("pedro"), new AgentName("PEDRO")));
    }

    [Fact]
    public void UnassignedTicket_IsOnlyForSupervision()
    {
        Assert.Equal(["supervision"], TicketAudience.StaffFor(Tickets.Assigned(null), previousAgent: null));
    }

    [Fact]
    public void Groups_ComeFromTheUserClaims()
    {
        Assert.Equal(["agent:pedro"], TicketAudience.GroupsOf(Users.Agent("pedro")));
        Assert.Equal(["supervision"], TicketAudience.GroupsOf(Users.Supervisor()));
    }
}
