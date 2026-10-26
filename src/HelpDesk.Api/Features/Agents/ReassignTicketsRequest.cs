using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Agents;

public sealed record ReassignTicketsRequest(
    [Required(ErrorMessage = "El agente destino es obligatorio.")]
    [StringLength(50, ErrorMessage = "El agente no puede superar {1} caracteres.")]
    string To);
