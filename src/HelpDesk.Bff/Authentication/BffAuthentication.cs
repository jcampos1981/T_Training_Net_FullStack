using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HelpDesk.Bff.Authentication;

// Dos esquemas que trabajan juntos:
// - OpenID Connect: solo para iniciar sesión. Lleva al usuario a Keycloak, recibe el código y lo cambia por tokens.
// - Cookie: la sesión del navegador con el BFF. Guarda (cifrados) los tokens que entregó Keycloak.
public static class BffAuthentication
{
    public const string CookieName = "__Host-helpdesk";

    public static IServiceCollection AddBffAuthentication(this IServiceCollection services)
    {
        services.AddOptions<KeycloakClientOptions>()
            .BindConfiguration(KeycloakClientOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AccessTokenRefresher>();
        services.AddScoped<BffCookieEvents>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Sin sesión, la cookie responde 401 (BffCookieEvents). Solo /bff/login manda a Keycloak.
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                // __Host-: el navegador solo acepta la cookie si es Secure, con Path=/ y sin Domain. Así ningún
                // subdominio ni otra ruta puede pisarla.
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;                       // JavaScript no puede leerla (ni un XSS)
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Strict: el navegador no la manda cuando la petición viene de otro sitio (primera barrera contra CSRF).
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);       // una jornada; se extiende si hay actividad
                options.SlidingExpiration = true;
                options.EventsType = typeof(BffCookieEvents);
            })
            .AddOpenIdConnect();

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<KeycloakClientOptions>, IHostEnvironment>((oidc, keycloakOptions, environment) =>
            {
                KeycloakClientOptions keycloak = keycloakOptions.Value;

                oidc.Authority = keycloak.Authority;
                oidc.ClientId = keycloak.ClientId;
                oidc.ClientSecret = keycloak.ClientSecret;
                oidc.RequireHttpsMetadata = !environment.IsDevelopment();

                // Authorization Code + PKCE, como Scalar, pero el canje del código lo hace el servidor con su secreto.
                // Además, si Keycloak lo ofrece (lo hace), los parámetros del login viajan por el canal de atrás
                // (PAR, Pushed Authorization Requests) y no en la URL que ve el navegador.
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.Scope.Add("email");

                // Guarda access, refresh e id token en la cookie (cifrada): la API los necesita, el navegador no.
                oidc.SaveTokens = true;

                // Los claims con su nombre del JWT ("preferred_username", "sid"), sin traducirlos a ClaimTypes.
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "preferred_username";
            });

        return services;
    }
}
