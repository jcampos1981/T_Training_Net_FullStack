using HelpDesk.Domain;
using Microsoft.Extensions.Options;

namespace HelpDesk.Importer.Reports;

// Escribe el reporte en stdout. El detalle de la importación (filas rechazadas) va al log.
public sealed class ConsoleReportWriter(IOptions<ReportOptions> options, TimeProvider clock)
{
    private const string Unassigned = "Sin asignar";

    private ReportOptions Settings => options.Value;

    public void Write(TicketReport report, int rejectedRows)
    {
        WriteHeader(report, rejectedRows);
        WriteSlaSummary(report);
        WriteWorkload(report);
        WriteOldestOpen(report);
        WriteDuplicates(report);
    }

    private void WriteHeader(TicketReport report, int rejectedRows)
    {
        WriteTitle($"Reporte de tickets al {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"{report.TotalTickets} tickets importados, {rejectedRows} filas rechazadas (detalle en el log)");
    }

    private static void WriteSlaSummary(TicketReport report)
    {
        WriteTitle("Estado de SLA");

        foreach (var (state, count) in report.CountBySlaState())
        {
            Console.WriteLine($"{state.DisplayName,-12} {count,3}");
        }
    }

    private void WriteWorkload(TicketReport report)
    {
        WriteTitle("Carga por agente (tickets abiertos)");

        foreach (var (agent, open, breached) in report.WorkloadByAgent(Settings.Team))
        {
            Console.WriteLine($"{agent,-12} {open,2} abiertos  {breached,2} vencidos");
        }

        Console.WriteLine($"{Unassigned,-12} {report.UnassignedOpenTickets(),2} abiertos");
    }

    private void WriteOldestOpen(TicketReport report)
    {
        WriteTitle($"Los {Settings.OldestOpenCount} abiertos más antiguos");

        foreach (Ticket ticket in report.OldestOpen(Settings.OldestOpenCount))
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
