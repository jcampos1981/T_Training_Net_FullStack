namespace HelpDesk.Domain.Tests;

public sealed class TicketIdTests
{
    [Fact]
    public void ToString_FormatsWithPrefixAndFiveDigits()
    {
        var id = new TicketId(42);

        Assert.Equal("T-00042", id.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_WithNonPositiveValue_Throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TicketId(value));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new TicketId(7), new TicketId(7));
        Assert.NotEqual(new TicketId(7), new TicketId(8));
    }
}
