using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HelpDesk.Admin.Authentication;

// Dos esquemas que trabajan juntos, como en el BFF:
// - OpenID Connect: solo para iniciar sesión. Lleva al usuario a Keycloak, recibe el código y lo cambia por tokens.
// - Cookie: la sesión del navegador con el panel. A diferencia del BFF, no guarda tokens (ver SessionTokenStore).
public static class AdminAuthentication
{
    public const string SessionIdClaim = "sid";
    public const string StaffPolicy = "Staff";

    // Otro nombre que el del BFF: las cookies no distinguen puertos, y los dos corren en "localhost".
    private const string CookieName = "__Host-helpdesk-admin";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8); // una jornada

    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services)
    {
        services.AddOptions<KeycloakClientOptions>()
            .BindConfiguration(KeycloakClientOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SessionTokenStore>();
        services.AddSingleton<AccessTokenRefresher>();
        services.AddSingleton<SessionAccessTokens>();
        services.AddScoped<AdminCookieEvents>();
        services.AddScoped<AdminOidcEvents>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Sin sesión se va directo a Keycloak: en el panel no hay nada que ver antes de iniciar sesión.
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax y no Strict (el BFF sí usa Strict): aquí el propio documento HTML exige sesión. Con Strict, al
                // volver de un Keycloak en otro dominio el navegador no mandaría la cookie, y se entraría en un ciclo
                // de logins. Lax la manda al navegar, pero no en un POST desde otro sitio ni al abrir el WebSocket.
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = true;
                options.AccessDeniedPath = "/acceso-denegado"; // un cliente que inicia sesión aquí
                options.EventsType = typeof(AdminCookieEvents);
            })
            .AddOpenIdConnect();

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<KeycloakClientOptions>>((oidc, keycloakOptions) =>
            {
                KeycloakClientOptions keycloak = keycloakOptions.Value;

                oidc.Authority = keycloak.Authority;
                oidc.MetadataAddress = keycloak.MetadataAddress;
                oidc.ClientId = keycloak.ClientId;
                oidc.ClientSecret = keycloak.ClientSecret;
                oidc.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;

                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.Scope.Add("email");

                oidc.SaveTokens = false; // AdminOidcEvents los guarda en el servidor
                oidc.EventsType = typeof(AdminOidcEvents);

                // Los claims con su nombre del JWT ("preferred_username", "sid", "roles"), sin traducirlos.
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "preferred_username";
                // El mapper "roles en el ID token" del client helpdesk-admin (deploy/keycloak/helpdesk-realm.json).
                oidc.TokenValidationParameters.RoleClaimType = "roles";
            });

        // Los roles deciden qué MUESTRA el panel; quien decide qué se PUEDE hacer sigue siendo la API, en cada llamada.
        services.AddAuthorizationBuilder()
            .AddPolicy(StaffPolicy, policy => policy.RequireRole(AdminRoles.Agent, AdminRoles.Supervisor, AdminRoles.Admin));

        services.AddCascadingAuthenticationState();

        return services;
    }
}
