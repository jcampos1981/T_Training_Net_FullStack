namespace HelpDesk.Importer.Domain;

public sealed class StandardSlaPolicy(TimeProvider clock) : ISlaPolicy
{
    private const double AtRiskThreshold = 0.8;

    public TimeSpan ResolutionTimeFor(TicketPriority priority) => priority switch
    {
        TicketPriority.Critical => TimeSpan.FromHours(4),
        TicketPriority.High => TimeSpan.FromHours(8),
        TicketPriority.Medium => TimeSpan.FromHours(24),
        TicketPriority.Low => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "Prioridad sin SLA definido.")
    };

    public SlaState Evaluate(Ticket ticket)
    {
        if (ticket.IsClosed)
        {
            return SlaState.Closed;
        }

        TimeSpan elapsed = clock.GetUtcNow() - ticket.CreatedAt;
        double consumed = elapsed / ResolutionTimeFor(ticket.Priority);

        return consumed switch
        {
            >= 1.0 => SlaState.Breached,
            >= AtRiskThreshold => SlaState.AtRisk,
            _ => SlaState.OnTrack
        };
    }
}
