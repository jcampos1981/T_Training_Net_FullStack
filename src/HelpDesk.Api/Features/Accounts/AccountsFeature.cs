using System.Security.Claims;
using HelpDesk.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace HelpDesk.Api.Features.Accounts;

// Cuentas de usuario con ASP.NET Core Identity: registro, login, renovación del token y "quién soy".
public static class AccountsFeature
{
    public static IServiceCollection AddAccountsFeature(this IServiceCollection services)
    {
        // AddIdentityApiEndpoints = Identity (contraseñas con hash, bloqueo tras intentos fallidos) + dos formas
        // de presentarse en cada petición: la cookie de Identity o un bearer token emitido por Identity.
        // Ese token NO es un JWT: es un paquete cifrado con Data Protection que solo esta API sabe leer.
        // Sirve para una app propia; para que otros sistemas validen tokens hace falta JWT (Bloque 2).
        services.AddIdentityApiEndpoints<IdentityUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Largo antes que complejidad (recomendación actual de NIST); el resto de las reglas, por defecto.
                options.Password.RequiredLength = 12;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<HelpDeskDbContext>();

        // Cerrado por defecto: todo endpoint sin [AllowAnonymous] exige un usuario autenticado. Un endpoint
        // nuevo nace protegido aunque alguien olvide ponerle [Authorize].
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

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

        app.MapGet("/api/me", GetCurrentUser)
            .WithTags("Autenticación")
            .WithName(nameof(GetCurrentUser))
            .WithSummary("El usuario actual tal como lo ve la API: nombre, agente y roles.");

        return app;
    }

    // ClaimsPrincipal llega solo: es el usuario que armó el middleware de autenticación con la cookie o el token.
    private static Ok<CurrentUserResponse> GetCurrentUser(ClaimsPrincipal user) =>
        TypedResults.Ok(new CurrentUserResponse(
            user.Identity?.Name ?? string.Empty,
            user.FindFirstValue(HelpDeskClaimTypes.Agent),
            [.. user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)]));
}
