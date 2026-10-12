using System.Runtime.CompilerServices;

namespace HelpDesk.Importer.Import;

public static class TicketCsvReader
{
    private const int HeaderLines = 1;

    // Lee el archivo línea por línea, sin cargarlo completo en memoria.
    public static async IAsyncEnumerable<ParseResult> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int lineNumber = 0;

        await foreach (string line in File.ReadLinesAsync(path, cancellationToken))
        {
            lineNumber++;

            if (lineNumber <= HeaderLines || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return TicketCsvParser.Parse(line, lineNumber);
        }
    }
}
