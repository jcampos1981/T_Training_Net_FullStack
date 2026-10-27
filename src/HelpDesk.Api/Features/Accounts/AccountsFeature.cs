using System.Security.Claims;
using HelpDesk.Api.Data;
using HelpDesk.Api.Features.Accounts.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HelpDesk.Api.Features.Accounts;

// Cuentas de usuario con ASP.NET Core Identity (registro, login, "quién soy") y JWT propios (/api/token).
public static class AccountsFeature
{
    // No autentica nada: mira la petición y la manda al esquema que corresponde (JWT o Identity).
    private const string SchemeSelector = "HelpDesk";

    public static IServiceCollection AddAccountsFeature(this IServiceCollection services)
    {
        // AddIdentityApiEndpoints = Identity (contraseñas con hash, bloqueo tras intentos fallidos) + dos formas
        // de presentarse en cada petición: la cookie de Identity o un bearer token emitido por Identity.
        // Ese token NO es un JWT: es un paquete cifrado con Data Protection que solo esta API sabe leer.
        services.AddIdentityApiEndpoints<IdentityUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Largo antes que complejidad (recomendación actual de NIST); el resto de las reglas, por defecto.
                options.Password.RequiredLength = 12;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<HelpDeskDbContext>();

        AddJwtTokens(services);

        // Cerrado por defecto: todo endpoint sin [AllowAnonymous] exige un usuario autenticado. Un endpoint
        // nuevo nace protegido aunque alguien olvide ponerle [Authorize].
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddHelpDeskPolicies(); // Staff y Supervision

        return services;
    }

    public static IEndpointRouteBuilder MapAccountsFeature(this IEndpointRouteBuilder app)
    {
        // /register, /login, /refresh, /confirmEmail, /forgotPassword, /resetPassword y /manage/...
        // MapIdentityApi no marca sus endpoints públicos como [AllowAnonymous]: con "cerrado por defecto", hasta
        // /login exigiría estar autenticado y nadie podría entrar. Se marcan aquí todos MENOS /manage/..., que sí
        // necesitan al usuario. (Un [AllowAnonymous] en todo el grupo abriría también /manage.)
        app.MapGroup("/api/auth")
            .WithTags("Autenticación")
            .MapIdentityApi<IdentityUser>()
            .Add(endpoint =>
            {
                if (endpoint is RouteEndpointBuilder { RoutePattern.RawText: { } route } && !route.Contains("/manage/"))
                {
                    endpoint.Metadata.Add(new AllowAnonymousAttribute());
                }
            });

        app.MapTokenEndpoints(); // /api/token: JWT propios

        app.MapGet("/api/me", GetCurrentUser)
            .WithTags("Autenticación")
            .WithName(nameof(GetCurrentUser))
            .WithSummary("El usuario actual tal como lo ve la API: nombre, agente, roles y con qué se autenticó.");

        return app;
    }

    private static void AddJwtTokens(IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(jwt => jwt.HasValidSigningKey(),
                "Jwt:SigningKey debe ser Base64 de al menos 32 bytes. En desarrollo: dotnet user-secrets set \"Jwt:SigningKey\" \"...\"")
            .ValidateOnStart(); // sin una clave válida, la API no arranca (mejor que fallar en el primer login)

        services.AddSingleton<AccessTokenIssuer>();
        services.AddScoped<RefreshTokenStore>();
        services.AddScoped<TokenService>();

        // Conviven dos tipos de credencial: el JWT propio (/api/token) y lo de Identity (cookie o su token).
        // Este AddAuthentication va DESPUÉS de AddIdentityApiEndpoints, así que su esquema por defecto manda.
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SchemeSelector;
                options.DefaultChallengeScheme = SchemeSelector;
            })
            .AddPolicyScheme(SchemeSelector, "JWT o Identity", options =>
            {
                options.ForwardDefaultSelector = SelectScheme;
                // Responder "no autenticado" (401) o "prohibido" (403) siempre como JwtBearer: el esquema de la
                // cookie respondería con un 302 a una página de login que una API no tiene.
                options.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme;
                options.ForwardForbid = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer();

        // Las opciones de JwtBearer salen de JwtOptions (que a su vez sale de la configuración y de User Secrets).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                JwtOptions jwt = jwtOptions.Value;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    // Solo HS256: un token que diga en su encabezado "alg": "none" (u otro) se rechaza.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    // Por defecto se toleran 5 minutos de diferencia de reloj después de "exp"; con tokens de 15 min, 30 s.
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // JwtBearer traduce los nombres cortos del JWT (email, role, sub) a los de .NET (ClaimTypes.*),
                    // así el usuario se ve igual venga de un JWT o de Identity.
                    NameClaimType = ClaimTypes.Email,
                    RoleClaimType = ClaimTypes.Role
                };
            });
    }

    // Quién valida la credencial de esta petición:
    // - "Bearer" con un JWT (tres partes en Base64Url separadas por puntos: encabezado.contenido.firma) → JwtBearer.
    // - "Bearer" con otra cosa (el token de Identity no tiene puntos) → el bearer token de Identity.
    // - Sin header Authorization → la cookie de Identity (si no hay cookie, el usuario queda anónimo).
    private static string SelectScheme(HttpContext context)
    {
        string? authorization = context.Request.Headers.Authorization;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return IdentityConstants.ApplicationScheme;
        }

        return authorization.Count(character => character == '.') == 2
            ? JwtBearerDefaults.AuthenticationScheme
            : IdentityConstants.BearerScheme;
    }

    // ClaimsPrincipal llega solo: es el usuario que armó el middleware de autenticación con la cookie o el token.
    private static Ok<CurrentUserResponse> GetCurrentUser(ClaimsPrincipal user, HttpContext context) =>
        TypedResults.Ok(new CurrentUserResponse(
            user.Identity?.Name ?? string.Empty,
            user.FindFirstValue(HelpDeskClaimTypes.Agent),
            [.. user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)],
            context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket?.AuthenticationScheme));
}
