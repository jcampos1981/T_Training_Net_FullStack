using System.Security.Claims;
using System.Text.Json;

namespace HelpDesk.Api.Features.Accounts.Keycloak;

// Traduce los claims de Keycloak a los que usa el resto de la API (patrón Adapter, o "capa anticorrupción"): las
// policies y TicketAuthorizationHandler no saben quién emitió el token, y no hay que tocarlos al sumar un proveedor.
//
//   Keycloak                                                    HelpDesk
//   realm_access: { "roles": ["Agent", "offline_access", ...] }  →  ClaimTypes.Role = "Agent" (solo los conocidos)
//   preferred_username: "pedro"  (si tiene el rol Agent)          →  helpdesk:agent = "pedro"
public static class KeycloakClaims
{
    private const string RealmAccess = "realm_access";
    private const string PreferredUsername = "preferred_username";

    public static void AddHelpDeskClaims(ClaimsIdentity identity)
    {
        IReadOnlyList<string> roles = ReadRealmRoles(identity);

        foreach (string role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        // En Keycloak el usuario se llama como el agente ("pedro"). En Identity el usuario es el email, y por eso
        // hubo que guardar el claim helpdesk:agent aparte.
        if (roles.Contains(HelpDeskRoles.Agent) && identity.FindFirst(PreferredUsername)?.Value is { } username)
        {
            identity.AddClaim(new Claim(HelpDeskClaimTypes.Agent, username));
        }
    }

    // Keycloak anida los roles del realm en un objeto JSON, y el claim llega como ese texto: hay que parsearlo.
    // Se quedan solo los roles que la API conoce; los internos de Keycloak (offline_access, default-roles-helpdesk)
    // no le dicen nada.
    private static IReadOnlyList<string> ReadRealmRoles(ClaimsIdentity identity)
    {
        if (identity.FindFirst(RealmAccess)?.Value is not { } json)
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("roles", out JsonElement roles) || roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. roles.EnumerateArray()
            .Select(role => role.GetString())
            .OfType<string>() // descarta los null
            .Where(role => HelpDeskRoles.All.Contains(role))];
    }
}
