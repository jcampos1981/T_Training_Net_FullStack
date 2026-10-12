namespace HelpDesk.Importer.Domain;

public sealed class StandardSlaPolicy(SlaOptions options, TimeProvider clock) : ISlaPolicy
{
    public TimeSpan ResolutionTimeFor(TicketPriority priority) =>
        options.ResolutionHours.TryGetValue(priority, out double hours)
            ? TimeSpan.FromHours(hours)
            : throw new ArgumentOutOfRangeException(nameof(priority), priority, "Prioridad sin SLA definido.");

    public SlaState Evaluate(Ticket ticket)
    {
        if (ticket.IsClosed)
        {
            return SlaState.Closed;
        }

        TimeSpan elapsed = clock.GetUtcNow() - ticket.CreatedAt;
        double consumed = elapsed / ResolutionTimeFor(ticket.Priority);

        // El umbral viene de configuración y un patrón solo acepta constantes: por eso va en un "when".
        return consumed switch
        {
            >= 1.0 => SlaState.Breached,
            _ when consumed >= options.AtRiskThreshold => SlaState.AtRisk,
            _ => SlaState.OnTrack
        };
    }
}
