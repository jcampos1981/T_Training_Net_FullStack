using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Security;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    // Por usuario (o por IP si es anónimo): una cubeta de fichas. Cada petición gasta una; se reponen solas.
    // Permite ráfagas cortas (una pantalla que dispara 10 llamadas juntas) pero no un ritmo alto sostenido.
    [Range(1, 10_000)]
    public int UserBurst { get; set; } = 60;

    [Range(6, 60_000)]
    public int UserRequestsPerMinute { get; set; } = 120;

    // Login y emisión de tokens, por IP: frena a quien prueba contraseñas en masa.
    [Range(1, 1_000)]
    public int LoginAttemptsPerMinute { get; set; } = 10;
}
