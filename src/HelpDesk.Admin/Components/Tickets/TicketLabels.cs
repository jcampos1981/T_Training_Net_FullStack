using HelpDesk.Admin.Api.Generated.Models;
using MudBlazor;

namespace HelpDesk.Admin.Components.Tickets;

// Sin rama "_" a propósito: si la API agrega un valor y se regenera el cliente, CS8509 no deja compilar hasta que
// alguien lo agregue aquí (ver el .csproj). CS8524 solo avisa de valores sin nombre, como (TicketStatus)99.
#pragma warning disable CS8524
public static class TicketLabels
{
    public static string Of(TicketStatus status) => status switch
    {
        TicketStatus.Open => "Abierto",
        TicketStatus.InProgress => "En atención",
        TicketStatus.Resolved => "Resuelto",
        TicketStatus.Closed => "Cerrado"
    };

    public static string Of(TicketPriority priority) => priority switch
    {
        TicketPriority.Low => "Baja",
        TicketPriority.Medium => "Media",
        TicketPriority.High => "Alta",
        TicketPriority.Critical => "Crítica"
    };

    public static Color ColorOf(TicketStatus status) => status switch
    {
        TicketStatus.Open => Color.Info,
        TicketStatus.InProgress => Color.Warning,
        TicketStatus.Resolved => Color.Success,
        TicketStatus.Closed => Color.Default
    };

    public static Color ColorOf(TicketPriority priority) => priority switch
    {
        TicketPriority.Low => Color.Default,
        TicketPriority.Medium => Color.Info,
        TicketPriority.High => Color.Warning,
        TicketPriority.Critical => Color.Error
    };
}
#pragma warning restore CS8524
