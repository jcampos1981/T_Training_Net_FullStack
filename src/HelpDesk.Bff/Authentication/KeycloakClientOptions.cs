using System.ComponentModel.DataAnnotations;

namespace HelpDesk.Bff.Authentication;

// El BFF es un client CONFIDENCIAL de Keycloak: corre en el servidor, así que sí puede guardar un secreto
// (a diferencia de Scalar o de un SPA, que corren en el navegador y cualquiera puede leer su código).
public sealed class KeycloakClientOptions
{
    public const string SectionName = "Keycloak";

    [Required, Url]
    public string Authority { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    // En User Secrets (desarrollo) o en un almacén de secretos (producción), nunca en appsettings:
    //   dotnet user-secrets set "Keycloak:ClientSecret" "<secreto>" --project src/HelpDesk.Bff
    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    // Cuánto antes de que venza el access token se pide uno nuevo. El de Keycloak dura 5 minutos.
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromMinutes(1);
}
