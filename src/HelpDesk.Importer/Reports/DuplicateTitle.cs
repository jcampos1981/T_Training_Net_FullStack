using HelpDesk.Importer.Domain;

namespace HelpDesk.Importer.Reports;

public sealed record DuplicateTitle(string Title, IReadOnlyList<TicketId> TicketIds);
