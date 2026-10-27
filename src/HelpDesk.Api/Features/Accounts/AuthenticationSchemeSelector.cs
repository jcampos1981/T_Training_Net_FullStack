using HelpDesk.Api.Features.Accounts.Keycloak;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace HelpDesk.Api.Features.Accounts;

// La API acepta cuatro credenciales. Este esquema no autentica nada: mira la petición y la deriva al que corresponde.
//
//   Authorization: Bearer <JWT con iss = Keycloak>  → Keycloak    (RS256, llaves públicas del proveedor)
//   Authorization: Bearer <otro JWT>                → JwtBearer   (nuestros JWT de /api/token, HS256)
//   Authorization: Bearer <algo que no es un JWT>   → el bearer token de Identity (cifrado con Data Protection)
//   sin header Authorization                        → la cookie de Identity (si no hay, el usuario queda anónimo)
internal static class AuthenticationSchemeSelector
{
    public const string SchemeName = "HelpDesk";

    private const string BearerPrefix = "Bearer ";

    // Solo lee: no valida nada, así que una sola instancia sirve para todas las peticiones.
    private static readonly JsonWebTokenHandler TokenReader = new();

    public static string Select(HttpContext context)
    {
        string? authorization = context.Request.Headers.Authorization;
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return IdentityConstants.ApplicationScheme;
        }

        string token = authorization[BearerPrefix.Length..].Trim();

        // Un JWT son tres partes en Base64Url separadas por puntos; el token de Identity no tiene puntos.
        if (!TokenReader.CanReadToken(token))
        {
            return IdentityConstants.BearerScheme;
        }

        KeycloakOptions keycloak = context.RequestServices.GetRequiredService<IOptions<KeycloakOptions>>().Value;

        return keycloak.IsIssuer(PeekIssuer(token))
            ? KeycloakAuthentication.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
    }

    // Leer "iss" SIN validar la firma es seguro porque aquí solo se elige QUIÉN valida: el esquema elegido revisa
    // firma, emisor, audiencia y vencimiento. Un "iss" inventado solo manda el token a un esquema que lo rechaza.
    private static string? PeekIssuer(string token)
    {
        try
        {
            return TokenReader.ReadJsonWebToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            return null; // tiene puntos pero no es un JWT legible: JwtBearer responderá 401
        }
    }
}
