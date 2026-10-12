using HelpDesk.Importer.Domain;

namespace HelpDesk.Importer.Import;

public sealed record ImportResult(
    string Source,
    IReadOnlyList<Ticket> Imported,
    IReadOnlyList<ParseResult.Failure> Rejected);
