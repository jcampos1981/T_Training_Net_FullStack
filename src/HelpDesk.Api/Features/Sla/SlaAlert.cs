using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Sla;

// "Ya se avisó que este ticket llegó a este nivel". Una tabla aparte y no una columna en Tickets: cualquier UPDATE de un
// ticket cambia su rowversion, y una alerta le provocaría un conflicto de edición falso a quien lo esté editando.
public sealed class SlaAlert
{
    public int Id { get; init; }

    public required TicketId TicketId { get; init; }

    public required SlaState Level { get; init; }

    public required DateTimeOffset RaisedAt { get; init; }
}
