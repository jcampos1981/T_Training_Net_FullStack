using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Accounts.Tokens;

public sealed record TokenRequest(
    [Required(ErrorMessage = "El email es obligatorio.")] string Email,
    [Required(ErrorMessage = "La contraseña es obligatoria.")] string Password);

public sealed record RefreshTokenRequest(
    [Required(ErrorMessage = "El refresh token es obligatorio.")] string RefreshToken);
