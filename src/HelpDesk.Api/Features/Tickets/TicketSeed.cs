using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// Datos de arranque mientras no hay base de datos (llega en la Fase 3).
public static class TicketSeed
{
    public static IEnumerable<Ticket> Create(DateTimeOffset now) =>
    [
        new Ticket { Id = new(1), Title = "No puedo iniciar sesión", Priority = TicketPriority.High, CreatedAt = now.AddHours(-5), AssignedTo = "ana" },
        new Ticket { Id = new(2), Title = "Impresora del piso 2 sin tóner", Priority = TicketPriority.Low, CreatedAt = now.AddDays(-2) },
        new Ticket { Id = new(3), Title = "Error 500 al guardar factura", Priority = TicketPriority.Critical, Status = TicketStatus.InProgress, CreatedAt = now.AddHours(-2), AssignedTo = "luis" },
        new Ticket { Id = new(4), Title = "Solicitud de cuenta nueva", Priority = TicketPriority.Medium, Status = TicketStatus.Resolved, CreatedAt = now.AddDays(-3), AssignedTo = "ana" },
        new Ticket { Id = new(5), Title = "VPN se desconecta", Priority = TicketPriority.High, CreatedAt = now.AddHours(-9), AssignedTo = "marta" }
    ];
}
