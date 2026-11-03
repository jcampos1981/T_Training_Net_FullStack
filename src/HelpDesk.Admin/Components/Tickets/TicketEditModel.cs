using System.ComponentModel.DataAnnotations;
using HelpDesk.Admin.Api.Generated.Models;

namespace HelpDesk.Admin.Components.Tickets;

// Lo que se edita en el formulario. Una clase mutable aparte del contrato de la API, porque EditForm escribe aquí con
// cada cambio. Las reglas repiten las de UpdateTicketRequest a propósito: dan respuesta inmediata, sin ir a la API.
// Quien manda sigue siendo la API: si algún día difieren, ApiValidationErrors muestra su mensaje igual.
public sealed class TicketEditModel
{
    [Required(ErrorMessage = "El título es obligatorio.")]
    [StringLength(120, MinimumLength = 5, ErrorMessage = "El título debe tener entre {2} y {1} caracteres.")]
    public string? Title { get; set; }

    public TicketPriority Priority { get; set; }

    public TicketStatus Status { get; set; }

    [StringLength(50, ErrorMessage = "El agente no puede superar {1} caracteres.")]
    public string? AssignedTo { get; set; }

    // Priority y Status son obligatorios en el contrato, aunque Kiota los deje como anulables.
    public static TicketEditModel From(TicketResponse ticket) => new()
    {
        Title = ticket.Title,
        Priority = ticket.Priority!.Value,
        Status = ticket.Status!.Value,
        AssignedTo = ticket.AssignedTo
    };

    // PUT reemplaza el ticket completo: AssignedTo vacío significa "sin asignar".
    public UpdateTicketRequest ToRequest(string? version) => new()
    {
        Title = Title,
        Priority = Priority,
        Status = Status,
        AssignedTo = string.IsNullOrWhiteSpace(AssignedTo) ? null : AssignedTo.Trim(),
        Version = version
    };
}
