using System.Globalization;
using HelpDesk.Domain;

namespace HelpDesk.Importer.Import;

// Separación simple por ';': suficiente para aprender. Un CSV real (comillas, saltos de línea
// dentro de campos) se lee con una librería como CsvHelper.
public static class TicketCsvParser
{
    private const char Separator = ';';

    public static ParseResult Parse(string line, int lineNumber)
    {
        string[] fields = line.Split(Separator, StringSplitOptions.TrimEntries);

        TicketRow? row = fields switch
        {
            [var id, var title, var priority, var status, var createdAt] =>
                new(id, title, priority, status, createdAt, AssignedTo: null),
            [var id, var title, var priority, var status, var createdAt, var assignedTo] =>
                new(id, title, priority, status, createdAt, assignedTo),
            _ => null
        };

        return row is null
            ? new ParseResult.Failure(lineNumber, $"Se esperaban 5 o 6 columnas y hay {fields.Length}.")
            : ToTicket(row, lineNumber);
    }

    private static ParseResult ToTicket(TicketRow row, int lineNumber)
    {
        if (!int.TryParse(row.Id, out int id) || id <= 0)
        {
            return Fail($"Id inválido: '{row.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(row.Title))
        {
            return Fail("El título está vacío.");
        }

        if (!TryParseDefined(row.Priority, out TicketPriority priority))
        {
            return Fail($"Prioridad desconocida: '{row.Priority}'.");
        }

        if (!TryParseDefined(row.Status, out TicketStatus status))
        {
            return Fail($"Estado desconocido: '{row.Status}'.");
        }

        if (!DateTimeOffset.TryParse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset createdAt))
        {
            return Fail($"Fecha inválida: '{row.CreatedAt}'.");
        }

        var ticket = new Ticket
        {
            Id = new TicketId(id),
            Title = row.Title,
            Priority = priority,
            Status = status,
            CreatedAt = createdAt,
            AssignedTo = string.IsNullOrWhiteSpace(row.AssignedTo) ? null : row.AssignedTo
        };

        return new ParseResult.Success(ticket);

        ParseResult Fail(string reason) => new ParseResult.Failure(lineNumber, reason);
    }

    // Enum.TryParse acepta números ("7") aunque no correspondan a ningún valor: se rechazan.
    private static bool TryParseDefined<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        return !int.TryParse(text, out _)
            && Enum.TryParse(text, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }

    private sealed record TicketRow(
        string Id,
        string Title,
        string Priority,
        string Status,
        string CreatedAt,
        string? AssignedTo);
}
