using HelpDesk.Domain;
using HelpDesk.Importer.Import;
using HelpDesk.Importer.Reports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HelpDesk.Importer;

public sealed class ImportApp(
    TicketImporter importer,
    ISlaPolicy slaPolicy,
    ConsoleReportWriter reportWriter,
    IHostEnvironment environment,
    IOptions<ImportOptions> importOptions,
    ILogger<ImportApp> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string folder = Path.GetFullPath(importOptions.Value.Folder, environment.ContentRootPath);
        string[] csvFiles = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.csv").Order()]
            : [];

        if (csvFiles.Length == 0)
        {
            logger.LogError("No hay archivos .csv en {Folder}", folder);
            return ExitCodes.NoInput;
        }

        try
        {
            ImportResult[] results = await importer.ImportAllAsync(csvFiles, cancellationToken);

            List<Ticket> tickets = [.. results.SelectMany(result => result.Imported)];
            int rejectedRows = results.Sum(result => result.Rejected.Count);

            reportWriter.Write(new TicketReport(tickets, slaPolicy), rejectedRows);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Importación cancelada por el usuario");
            return ExitCodes.Cancelled;
        }
    }
}
