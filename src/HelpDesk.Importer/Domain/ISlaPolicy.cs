namespace HelpDesk.Importer.Domain;

public interface ISlaPolicy
{
    TimeSpan ResolutionTimeFor(TicketPriority priority);

    SlaState Evaluate(Ticket ticket);
}
