using System.Text;
using HelpDesk.Importer.Domain;
using HelpDesk.Importer.Import;

Console.OutputEncoding = Encoding.UTF8;

TimeProvider clock = TimeProvider.System;
ISlaPolicy slaPolicy = new StandardSlaPolicy(clock);
DateTimeOffset now = clock.GetUtcNow();

// Fechas relativas a "ahora" para que el ejemplo muestre todos los estados de SLA.
string sampleCsv = $"""
    Id;Titulo;Prioridad;Estado;Creado;Asignado
    1;No puedo iniciar sesión;Critical;Open;{now.AddHours(-5):O};ana
    2;Impresora sin tóner;Low;Open;{now.AddHours(-60):O}
    3;Error 500 al guardar factura;High;InProgress;{now.AddHours(-2):O};luis
    4;Solicitud de cuenta nueva;Medium;Resolved;{now.AddDays(-3):O};ana
    5;Prioridad que no existe;Urgente;Open;{now:O}
    6;Fila incompleta;High
    """;

List<Ticket> imported = [];
List<ParseResult.Failure> rejected = [];

foreach (var (line, lineNumber) in DataLines(sampleCsv))
{
    switch (TicketCsvParser.Parse(line, lineNumber))
    {
        case ParseResult.Success(var ticket):
            imported.Add(ticket);
            break;
        case ParseResult.Failure failure:
            rejected.Add(failure);
            break;
    }
}

Console.WriteLine($"Importados: {imported.Count} | Rechazados: {rejected.Count}");
Console.WriteLine();

foreach (Ticket ticket in imported)
{
    SlaState sla = slaPolicy.Evaluate(ticket);
    Console.WriteLine($"{ticket.Id}  {ticket.Priority.DisplayName,-8} {sla.DisplayName,-10} {ticket.Title}");
}

Console.WriteLine();

foreach (var (lineNumber, reason) in rejected)
{
    Console.WriteLine($"Línea {lineNumber}: {reason}");
}

static IEnumerable<(string Line, int LineNumber)> DataLines(string csv) =>
    csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
       .Select((line, index) => (line, index + 1))
       .Skip(1); // encabezado
