using System.Text;
using HelpDesk.Importer.Common;
using HelpDesk.Importer.Domain;
using HelpDesk.Importer.Import;
using HelpDesk.Importer.Reports;

Console.OutputEncoding = Encoding.UTF8;

string[] team = ["ana", "luis", "marta", "pedro", "sofia", "jorge"];
string defaultFolder = Path.Combine(AppContext.BaseDirectory, "samples");

if (ImporterArguments.Parse(args, defaultFolder) is not { } arguments)
{
    Console.Error.WriteLine(ImporterArguments.Usage);
    return 1;
}

string[] csvFiles = Directory.Exists(arguments.Folder)
    ? [.. Directory.EnumerateFiles(arguments.Folder, "*.csv").Order()]
    : [];

if (csvFiles.Length == 0)
{
    Console.Error.WriteLine($"No hay archivos .csv en '{arguments.Folder}'.");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true; // Ctrl+C no mata el proceso: cancela la importación de forma ordenada.
    cancellation.Cancel();
};

try
{
    ImportResult[] results = await new TicketImporter().ImportAllAsync(csvFiles, cancellation.Token);
    List<Ticket> tickets = [.. results.SelectMany(result => result.Imported)];

    TimeProvider clock = arguments.CreateClock();
    var report = new TicketReport(tickets, new StandardSlaPolicy(clock));

    ConsoleReportWriter.WriteImportSummary(results);
    ConsoleReportWriter.WriteReport(report, team, clock.GetUtcNow());
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Importación cancelada.");
    return 2;
}
