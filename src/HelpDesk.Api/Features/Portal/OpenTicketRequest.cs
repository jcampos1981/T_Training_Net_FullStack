using System.ComponentModel.DataAnnotations;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Portal;

// Lo que llena el cliente en el portal. La descripción se guarda como el primer comentario del ticket.
public sealed record OpenTicketRequest(
    [Required(ErrorMessage = "El título es obligatorio.")]
    [StringLength(120, MinimumLength = 5, ErrorMessage = "El título debe tener entre {2} y {1} caracteres.")]
    string Title,
    [Required(ErrorMessage = "La descripción es obligatoria.")]
    [StringLength(2000, MinimumLength = 10, ErrorMessage = "La descripción debe tener entre {2} y {1} caracteres.")]
    string Description,
    TicketPriority Priority = TicketPriority.Medium) : IValidatableObject
{
    // Critical lo decide el equipo de soporte al revisar el ticket: si cualquier cliente pudiera marcarlo,
    // todo terminaría siendo crítico.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Priority is TicketPriority.Critical)
        {
            yield return new ValidationResult(
                "Desde el portal la prioridad puede ser Low, Medium o High.", [nameof(Priority)]);
        }
    }
}
