using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HelpDesk.Api.Data;

// TicketId en C# ↔ int en la base. Se registra una sola vez como convención para todo el modelo
// (Ticket.Id y TicketComment.TicketId), en vez de repetirlo en cada configuración.
public sealed class TicketIdConverter() : ValueConverter<TicketId, int>(
    id => id.Value,
    value => new TicketId(value));
