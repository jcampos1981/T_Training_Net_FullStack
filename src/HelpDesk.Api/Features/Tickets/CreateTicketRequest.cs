using System.ComponentModel.DataAnnotations;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

public sealed record CreateTicketRequest(
    [Required(ErrorMessage = "El título es obligatorio.")]
    [StringLength(120, MinimumLength = 5, ErrorMessage = "El título debe tener entre {2} y {1} caracteres.")]
    string Title,
    TicketPriority Priority,
    [StringLength(50, ErrorMessage = "El agente no puede superar {1} caracteres.")]
    string? AssignedTo = null);
