using HelpDesk.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HelpDesk.Api.Data;

// AgentName en C# ↔ nvarchar en la base. Igual que TicketId, se registra una sola vez como convención.
public sealed class AgentNameConverter() : ValueConverter<AgentName, string>(
    name => name.Value,
    value => new AgentName(value));
