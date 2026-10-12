using HelpDesk.Importer.Domain;

namespace HelpDesk.Importer.Import;

public sealed class TicketImporter
{
    public async Task<ImportResult> ImportFileAsync(string path, CancellationToken cancellationToken)
    {
        List<Ticket> imported = [];
        List<ParseResult.Failure> rejected = [];

        await foreach (ParseResult result in TicketCsvReader.ReadAsync(path, cancellationToken))
        {
            switch (result)
            {
                case ParseResult.Success(var ticket):
                    imported.Add(ticket);
                    break;
                case ParseResult.Failure failure:
                    rejected.Add(failure);
                    break;
            }
        }

        return new ImportResult(Path.GetFileName(path), imported, rejected);
    }

    // Todos los archivos se leen a la vez. Cada importación usa sus propias listas,
    // así que no comparten estado y no hace falta sincronizar nada.
    public Task<ImportResult[]> ImportAllAsync(IEnumerable<string> paths, CancellationToken cancellationToken) =>
        Task.WhenAll(paths.Select(path => ImportFileAsync(path, cancellationToken)));
}
