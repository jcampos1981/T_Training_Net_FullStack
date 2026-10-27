using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Comments;

// Sin "author": el autor es el usuario autenticado. Si el cliente pudiera mandarlo, cualquiera podría firmar
// un comentario como otra persona.
public sealed record AddCommentRequest(
    [Required(ErrorMessage = "El comentario es obligatorio.")]
    [StringLength(2000, ErrorMessage = "El comentario no puede superar {1} caracteres.")]
    string Body);
