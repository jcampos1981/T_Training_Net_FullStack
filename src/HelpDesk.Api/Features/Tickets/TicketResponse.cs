using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// DTO: lo que la API expone. Se separa de la entidad para poder cambiar el dominio sin romper a los clientes.
public sealed record TicketResponse(
    int Id,
    string Code,
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    string? AssignedTo,
    // Texto y no número: JavaScript pierde precisión con enteros de más de 2^53.
    string Version,
    // Al final a propósito: agregar un campo nuevo no rompe a los clientes que ya leen esta respuesta.
    string? RequestedBy);
