using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

// PUT reemplaza el ticket completo: si AssignedTo no viene, el ticket queda sin asignar.
// Version: la que devolvió el GET. Si ya no es la actual, alguien más guardó antes → 409.
public sealed record UpdateTicketRequest(
    [Required(ErrorMessage = "El título es obligatorio.")]
    [StringLength(120, MinimumLength = 5, ErrorMessage = "El título debe tener entre {2} y {1} caracteres.")]
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    [Required(ErrorMessage = "La versión es obligatoria: usa la que devolvió el GET del ticket.")]
    [RegularExpression("^[0-9]{1,20}$", ErrorMessage = "La versión no es válida.")]
    string Version,
    [StringLength(50, ErrorMessage = "El agente no puede superar {1} caracteres.")]
    string? AssignedTo = null)
{
    // Un número que no cabe en ulong no puede coincidir con ninguna versión real: se trata como 0 (→ 409).
    public ulong ExpectedVersion() =>
        ulong.TryParse(Version, NumberStyles.None, CultureInfo.InvariantCulture, out ulong version) ? version : 0;
}
