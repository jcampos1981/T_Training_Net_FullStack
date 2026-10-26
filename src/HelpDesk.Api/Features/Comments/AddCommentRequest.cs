using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Comments;

public sealed record AddCommentRequest(
    [Required(ErrorMessage = "El autor es obligatorio.")]
    [StringLength(50, ErrorMessage = "El autor no puede superar {1} caracteres.")]
    string Author,
    [Required(ErrorMessage = "El comentario es obligatorio.")]
    [StringLength(2000, ErrorMessage = "El comentario no puede superar {1} caracteres.")]
    string Body);
