namespace HelpDesk.Domain.Tests;

public sealed class AgentNameTests
{
    [Theory]
    [InlineData("pedro", "pedro")]
    [InlineData("Pedro", "pedro")]
    [InlineData("LUIS", "luis")]
    public void Equality_DoesNotDependOnCase(string left, string right)
    {
        Assert.Equal(new AgentName(left), new AgentName(right));
        Assert.Equal(new AgentName(left).GetHashCode(), new AgentName(right).GetHashCode());
    }

    [Fact]
    public void DifferentNames_AreDifferent()
    {
        Assert.NotEqual(new AgentName("pedro"), new AgentName("luis"));
    }

    [Fact]
    public void Value_IsTrimmed_AndKeepsItsCase()
    {
        Assert.Equal("Pedro", new AgentName("  Pedro ").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlank(string value)
    {
        Assert.Throws<ArgumentException>(() => new AgentName(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_TreatsBlankAsNoAgent(string? value)
    {
        Assert.Null(AgentName.From(value));
    }

    [Fact]
    public void Nullable_ComparesAgainstNoAgent()
    {
        AgentName? nobody = null;

        Assert.False(new AgentName("pedro") == nobody);
    }

    [Fact]
    public void Update_ChangingOnlyTheCase_IsTheSameAgent()
    {
        Ticket ticket = TestTickets.Create() with { AssignedTo = new AgentName("pedro") };

        Ticket updated = ticket.Update(ticket.Title, ticket.Priority, ticket.Status, new AgentName("PEDRO")).Value;

        Assert.Equal(ticket.AssignedTo, updated.AssignedTo);
    }
}
