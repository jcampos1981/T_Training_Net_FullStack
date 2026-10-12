namespace HelpDesk.Domain;

public interface ISlaPolicy
{
    TimeSpan ResolutionTimeFor(TicketPriority priority);

    SlaState Evaluate(Ticket ticket);
}
