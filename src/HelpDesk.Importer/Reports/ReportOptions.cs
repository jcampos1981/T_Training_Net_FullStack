namespace HelpDesk.Importer.Reports;

public sealed class ReportOptions
{
    public List<string> Team { get; set; } = [];

    public int OldestOpenCount { get; set; } = 5;

    // Sin valor: se usa la hora actual. Con valor: reporte reproducible "a esa fecha".
    public DateTimeOffset? AsOf { get; set; }
}
