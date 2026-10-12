using HelpDesk.Importer.Domain;

namespace HelpDesk.Importer.Reports;

// Cada método es una consulta LINQ sobre tickets ya cargados en memoria (LINQ to Objects).
public sealed class TicketReport(IReadOnlyCollection<Ticket> tickets, ISlaPolicy slaPolicy)
{
    private IEnumerable<Ticket> OpenTickets => tickets.Where(ticket => !ticket.IsClosed);

    public int TotalTickets => tickets.Count;

    public IReadOnlyList<(SlaState State, int Count)> CountBySlaState() =>
        tickets.CountBy(slaPolicy.Evaluate)
               .OrderBy(entry => entry.Key)
               .Select(entry => (entry.Key, entry.Value))
               .ToList();

    public IReadOnlyList<AgentWorkload> WorkloadByAgent(IEnumerable<string> team) =>
        team.GroupJoin(
                OpenTickets,
                agent => agent,
                ticket => ticket.AssignedTo,
                (agent, assigned) => new AgentWorkload(
                    agent,
                    OpenTickets: assigned.Count(),
                    BreachedTickets: assigned.Count(IsBreached)),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(workload => workload.BreachedTickets)
            .ThenByDescending(workload => workload.OpenTickets)
            .ToList();

    public int UnassignedOpenTickets() => OpenTickets.Count(ticket => ticket.AssignedTo is null);

    public IReadOnlyList<Ticket> OldestOpen(int count) =>
        OpenTickets.OrderBy(ticket => ticket.CreatedAt)
                   .Take(count)
                   .ToList();

    public IReadOnlyList<DuplicateTitle> DuplicateTitles() =>
        tickets.GroupBy(ticket => ticket.Title, StringComparer.OrdinalIgnoreCase)
               .Where(group => group.Count() > 1)
               .Select(group => new DuplicateTitle(group.Key, [.. group.Select(ticket => ticket.Id)]))
               .ToList();

    private bool IsBreached(Ticket ticket) => slaPolicy.Evaluate(ticket) is SlaState.Breached;
}
