using HelpDesk.Importer.Domain;
using HelpDesk.Importer.Import;

namespace HelpDesk.Importer.Reports;

public static class ConsoleReportWriter
{
    private const int OldestOpenCount = 5;
    private const string Unassigned = "Sin asignar";

    public static void WriteImportSummary(IEnumerable<ImportResult> results)
    {
        WriteTitle("Importación");

        foreach (ImportResult result in results)
        {
            Console.WriteLine($"{result.Source,-22} {result.Imported.Count,3} importados  {result.Rejected.Count,2} rechazados");

            foreach (var (lineNumber, reason) in result.Rejected)
            {
                Console.WriteLine($"    línea {lineNumber}: {reason}");
            }
        }
    }

    public static void WriteReport(TicketReport report, IEnumerable<string> team, DateTimeOffset asOf)
    {
        WriteSlaSummary(report, asOf);
        WriteWorkload(report, team);
        WriteOldestOpen(report);
        WriteDuplicates(report);
    }

    private static void WriteSlaSummary(TicketReport report, DateTimeOffset asOf)
    {
        WriteTitle($"Estado de SLA al {asOf:yyyy-MM-dd HH:mm} UTC");

        foreach (var (state, count) in report.CountBySlaState())
        {
            Console.WriteLine($"{state.DisplayName,-12} {count,3}");
        }
    }

    private static void WriteWorkload(TicketReport report, IEnumerable<string> team)
    {
        WriteTitle("Carga por agente (tickets abiertos)");

        foreach (var (agent, open, breached) in report.WorkloadByAgent(team))
        {
            Console.WriteLine($"{agent,-12} {open,2} abiertos  {breached,2} vencidos");
        }

        Console.WriteLine($"{Unassigned,-12} {report.UnassignedOpenTickets(),2} abiertos");
    }

    private static void WriteOldestOpen(TicketReport report)
    {
        WriteTitle($"Los {OldestOpenCount} abiertos más antiguos");

        foreach (Ticket ticket in report.OldestOpen(OldestOpenCount))
        {
            Console.WriteLine($"{ticket.Id}  {ticket.CreatedAt:yyyy-MM-dd HH:mm}  {ticket.Priority.DisplayName,-8} {ticket.Title}");
        }
    }

    private static void WriteDuplicates(TicketReport report)
    {
        WriteTitle("Títulos duplicados");

        foreach (var (title, ticketIds) in report.DuplicateTitles())
        {
            Console.WriteLine($"\"{title}\": {string.Join(", ", ticketIds)}");
        }
    }

    private static void WriteTitle(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }
}
