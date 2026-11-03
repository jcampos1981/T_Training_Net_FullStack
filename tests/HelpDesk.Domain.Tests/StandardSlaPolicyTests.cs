using Microsoft.Extensions.Time.Testing;

namespace HelpDesk.Domain.Tests;

// xUnit crea una instancia nueva de esta clase POR CADA prueba: el constructor funciona como
// "preparación" y ninguna prueba hereda el reloj adelantado por otra.
public sealed class StandardSlaPolicyTests
{
    private readonly FakeTimeProvider _clock = new(TestTickets.Now);
    private readonly StandardSlaPolicy _policy;

    public StandardSlaPolicyTests()
    {
        var options = new SlaOptions
        {
            ResolutionHours = new()
            {
                [TicketPriority.Critical] = 4,
                [TicketPriority.High] = 8,
                [TicketPriority.Medium] = 24,
                [TicketPriority.Low] = 72
            },
            AtRiskThreshold = 0.8
        };

        _policy = new StandardSlaPolicy(options, _clock);
    }

    [Theory]
    [InlineData(TicketPriority.Critical, 4)]
    [InlineData(TicketPriority.Low, 72)]
    public void ResolutionTimeFor_UsesConfiguredHours(TicketPriority priority, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), _policy.ResolutionTimeFor(priority));
    }

    // Ticket crítico: 4 h de plazo. Se prueban los bordes exactos, donde suelen esconderse los bugs.
    [Theory]
    [InlineData(0.0, SlaState.OnTrack)]
    [InlineData(2.0, SlaState.OnTrack)]   // 50 % del plazo
    [InlineData(3.2, SlaState.AtRisk)]    // justo 80 %: el umbral ya cuenta como "en riesgo"
    [InlineData(3.9, SlaState.AtRisk)]
    [InlineData(4.0, SlaState.Breached)]  // justo 100 %: ya está vencido
    [InlineData(10.0, SlaState.Breached)]
    public void Evaluate_CriticalTicket_DependsOnElapsedTime(double hoursElapsed, SlaState expected)
    {
        Ticket ticket = TestTickets.Create(priority: TicketPriority.Critical, createdAt: _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromHours(hoursElapsed)); // "máquina del tiempo": sin esperar de verdad

        Assert.Equal(expected, _policy.Evaluate(ticket));
    }

    // El monitor busca en SQL con estas fronteras, y Evaluate decide en memoria: si no coinciden, la pantalla mostraría
    // "en plazo" para un ticket con alerta, o al revés.
    [Theory]
    [InlineData(TicketPriority.Critical)]
    [InlineData(TicketPriority.High)]
    [InlineData(TicketPriority.Medium)]
    [InlineData(TicketPriority.Low)]
    public void Frontiers_MatchWhatEvaluateDecides(TicketPriority priority)
    {
        Ticket ticket = TestTickets.Create(priority: priority, createdAt: _clock.GetUtcNow());

        _clock.Advance(_policy.AtRiskAfter(priority) - TimeSpan.FromTicks(1));
        Assert.Equal(SlaState.OnTrack, _policy.Evaluate(ticket));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(SlaState.AtRisk, _policy.Evaluate(ticket));

        _clock.SetUtcNow(ticket.CreatedAt + _policy.ResolutionTimeFor(priority));
        Assert.Equal(SlaState.Breached, _policy.Evaluate(ticket));
    }

    [Fact]
    public void DeadlineOf_IsCreationPlusResolutionTime()
    {
        Ticket ticket = TestTickets.Create(priority: TicketPriority.High, createdAt: TestTickets.Now);

        Assert.Equal(TestTickets.Now.AddHours(8), _policy.DeadlineOf(ticket));
    }

    [Fact]
    public void Evaluate_ClosedTicket_IsClosedNoMatterHowMuchTimePassed()
    {
        Ticket ticket = TestTickets.Create(status: TicketStatus.Closed, createdAt: _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(SlaState.Closed, _policy.Evaluate(ticket));
    }

    [Fact]
    public void ResolutionTimeFor_PriorityWithoutConfiguration_Throws()
    {
        var policyWithoutHours = new StandardSlaPolicy(new SlaOptions(), _clock);

        Assert.Throws<ArgumentOutOfRangeException>(() => policyWithoutHours.ResolutionTimeFor(TicketPriority.High));
    }
}
