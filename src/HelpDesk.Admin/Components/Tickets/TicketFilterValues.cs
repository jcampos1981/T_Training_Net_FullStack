using HelpDesk.Admin.Api.Generated.Models;

namespace HelpDesk.Admin.Components.Tickets;

// Los filtros de la lista. Un record: dos filtros iguales son "==", y así la página sabe si de verdad cambiaron.
public sealed record TicketFilterValues(TicketStatus? Status, TicketPriority? Priority, string? Agent)
{
    public const string StatusKey = "estado";
    public const string PriorityKey = "prioridad";
    public const string AgentKey = "agente";

    public static readonly TicketFilterValues None = new(null, null, null);

    // La URL la puede escribir cualquiera: lo que no es un valor válido se ignora.
    public static TicketFilterValues FromQuery(string? status, string? priority, string? agent) =>
        new(ParseEnum<TicketStatus>(status), ParseEnum<TicketPriority>(priority), string.IsNullOrWhiteSpace(agent) ? null : agent.Trim());

    // Un valor null quita ese parámetro de la URL.
    public IReadOnlyDictionary<string, object?> ToQuery() => new Dictionary<string, object?>
    {
        [StatusKey] = Status?.ToString(),
        [PriorityKey] = Priority?.ToString(),
        [AgentKey] = Agent
    };

    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
