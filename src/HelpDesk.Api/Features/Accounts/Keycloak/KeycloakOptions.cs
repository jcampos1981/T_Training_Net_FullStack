using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Api.Features.Accounts.Keycloak;

// Dónde está el proveedor de identidad y cómo se llama esta API para él. Va en appsettings.{Ambiente}.json (no son
// secretos: la API solo usa llaves PÚBLICAS). Sin Keycloak:Authority, la API no acepta tokens de Keycloak.
public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    // La URL del realm. Es a la vez el "iss" de sus tokens y la base del discovery document
    // ({Authority}/.well-known/openid-configuration), de donde la API saca el resto.
    [Url]
    public string? Authority { get; set; }

    // El "aud" que deben traer los tokens: el client de Keycloak que representa a esta API. Un token emitido para
    // otra aplicación del mismo realm (otro "aud") se rechaza aunque la firma sea válida.
    [Required]
    public string Audience { get; set; } = "helpdesk-api";

    // El client público con el que Scalar inicia sesión (Authorization Code + PKCE). Opcional.
    public string? ScalarClientId { get; set; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority);

    // Rutas fijas de Keycloak; son las mismas que publica el discovery document.
    public Uri AuthorizationEndpoint => new($"{RealmUrl}/protocol/openid-connect/auth");

    public Uri TokenEndpoint => new($"{RealmUrl}/protocol/openid-connect/token");

    private string RealmUrl => Authority?.TrimEnd('/')
        ?? throw new InvalidOperationException($"Falta {SectionName}:Authority.");

    // ¿Lo emitió este Keycloak? Compara el "iss" del token con el Authority (sin la "/" final).
    public bool IsIssuer(string? issuer) =>
        IsEnabled && string.Equals(issuer?.TrimEnd('/'), RealmUrl, StringComparison.Ordinal);
}
