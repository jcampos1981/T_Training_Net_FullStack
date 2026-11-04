namespace HelpDesk.Domain.Tests;

public sealed class TicketTests
{
    [Fact]
    public void Title_IsTrimmed()
    {
        Ticket ticket = TestTickets.Create(title: "   Impresora sin tóner  ");

        Assert.Equal("Impresora sin tóner", ticket.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_WhenBlank_Throws(string title)
    {
        Assert.Throws<ArgumentException>(() => TestTickets.Create(title: title));
    }

    [Theory]
    [InlineData(TicketStatus.Open, false)]
    [InlineData(TicketStatus.InProgress, false)]
    [InlineData(TicketStatus.Resolved, true)]
    [InlineData(TicketStatus.Closed, true)]
    public void IsClosed_DependsOnStatus(TicketStatus status, bool expected)
    {
        Ticket ticket = TestTickets.Create(status: status);

        Assert.Equal(expected, ticket.IsClosed);
    }

    [Fact]
    public void Update_KeepsRequester()
    {
        // Arrange: un ticket abierto por un cliente desde el portal
        Ticket fromPortal = TestTickets.Create() with { RequestedBy = "camila@acme.local" };

        // Act: el equipo lo edita y lo asigna
        Ticket updated = fromPortal.Update("Título revisado", TicketPriority.High, TicketStatus.InProgress, new AgentName("pedro")).Value;

        // Assert: quién lo pidió no cambia
        Assert.Equal("camila@acme.local", updated.RequestedBy);
    }

    [Fact]
    public void Update_WhenOpen_ReturnsUpdatedCopyAndKeepsOriginal()
    {
        // Arrange
        Ticket original = TestTickets.Create(title: "Título original");

        // Act
        Ticket updated = original.Update("Título nuevo", TicketPriority.Critical, TicketStatus.InProgress, new AgentName("ana")).Value;

        // Assert
        Assert.Equal("Título nuevo", updated.Title);
        Assert.Equal(TicketPriority.Critical, updated.Priority);
        Assert.Equal(TicketStatus.InProgress, updated.Status);
        Assert.Equal(new AgentName("ana"), updated.AssignedTo);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("Título original", original.Title); // inmutable: el original no cambió
    }

    [Fact]
    public void Update_WhenClosed_FailsWithABusinessRule()
    {
        Ticket closed = TestTickets.Create(status: TicketStatus.Closed);

        Result<Ticket> result = closed.Update("Otro título", TicketPriority.Low, TicketStatus.Open, assignedTo: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("cerrado", result.Error.Detail);
    }

    [Fact]
    public void CreateComment_WhenOpen_ReturnsCommentLinkedToTicket()
    {
        Ticket ticket = TestTickets.Create();

        TicketComment comment = ticket.CreateComment("  luis ", "Ya revisé el servidor.", TestTickets.Now).Value;

        Assert.Equal(ticket.Id, comment.TicketId);
        Assert.Equal("luis", comment.Author);
        Assert.Equal(0, comment.Id); // todavía no se guarda: la base le asignará el Id
    }

    [Fact]
    public void CreateComment_WhenClosed_FailsWithABusinessRule()
    {
        Ticket closed = TestTickets.Create(status: TicketStatus.Closed);

        Result<TicketComment> result = closed.CreateComment("ana", "¿Sigue fallando?", TestTickets.Now);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    [Fact]
    public void Result_Value_WhenItFailed_IsABug()
    {
        Result<Ticket> failed = Error.NotFound;

        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Theory]
    [InlineData("", "Texto válido")]
    [InlineData("ana", "   ")]
    public void CreateComment_WithBlankAuthorOrBody_Throws(string author, string body)
    {
        Ticket ticket = TestTickets.Create();

        Assert.Throws<ArgumentException>(() => ticket.CreateComment(author, body, TestTickets.Now));
    }

    [Fact]
    public void Update_WhenResolved_IsAllowed()
    {
        Ticket resolved = TestTickets.Create(status: TicketStatus.Resolved);

        Ticket reopened = resolved.Update("Volvió a fallar", TicketPriority.High, TicketStatus.Open, assignedTo: null).Value;

        Assert.Equal(TicketStatus.Open, reopened.Status);
    }
}
