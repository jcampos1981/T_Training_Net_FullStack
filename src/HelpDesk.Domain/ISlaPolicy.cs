namespace HelpDesk.Domain;

public interface ISlaPolicy
{
    TimeSpan ResolutionTimeFor(TicketPriority priority);

    // Cuánto tiempo después de creado queda "en riesgo". Junto con ResolutionTimeFor son las dos fronteras de Evaluate:
    // permiten buscar en la base sin traer los tickets.
    TimeSpan AtRiskAfter(TicketPriority priority);

    DateTimeOffset DeadlineOf(Ticket ticket);

    SlaState Evaluate(Ticket ticket);
}
