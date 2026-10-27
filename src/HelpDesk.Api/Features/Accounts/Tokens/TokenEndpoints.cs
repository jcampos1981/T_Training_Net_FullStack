using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace HelpDesk.Api.Features.Accounts.Tokens;

// JWT propios: login, renovación y cierre de sesión. Es una versión simple del "token endpoint" de OAuth 2.0
// (que usa formularios y grant_type); para una app propia alcanza, y en el Bloque 4 lo hará Keycloak.
public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        // Anónimos a propósito: la credencial viene en el cuerpo (la contraseña o el refresh token).
        RouteGroupBuilder tokens = app.MapGroup("/api/token").WithTags("Tokens JWT").AllowAnonymous();

        tokens.MapPost("/", IssueToken)
            .WithName(nameof(IssueToken))
            .WithSummary("Inicia sesión con email y contraseña: devuelve un JWT de acceso (15 min) y un refresh token.")
            .ProducesValidationProblem();

        tokens.MapPost("/refresh", RefreshAccessToken)
            .WithName(nameof(RefreshAccessToken))
            .WithSummary("Cambia un refresh token por un par nuevo. Cada refresh token sirve una sola vez.")
            .ProducesValidationProblem();

        tokens.MapPost("/revoke", RevokeRefreshToken)
            .WithName(nameof(RevokeRefreshToken))
            .WithSummary("Cierra la sesión: revoca el refresh token y todos los de su familia.")
            .ProducesValidationProblem();

        return app;
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> IssueToken(
        TokenRequest request,
        UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        IdentityUser? user = await users.FindByEmailAsync(request.Email);

        // La misma respuesta para "no existe", "contraseña incorrecta" y "cuenta bloqueada": así no se puede
        // averiguar qué emails tienen cuenta. lockoutOnFailure cuenta los intentos fallidos, como /api/auth/login.
        if (user is null || !(await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true)).Succeeded)
        {
            return InvalidCredentials("Email o contraseña incorrectos.");
        }

        // Cada login empieza una familia nueva de refresh tokens.
        return TypedResults.Ok(await tokens.IssueAsync(user, Guid.NewGuid(), cancellationToken));
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> RefreshAccessToken(
        RefreshTokenRequest request,
        RefreshTokenStore refreshTokens,
        UserManager<IdentityUser> users,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        RefreshToken? redeemed = await refreshTokens.RedeemAsync(request.RefreshToken, cancellationToken);
        if (redeemed is null)
        {
            return InvalidCredentials("El refresh token no es válido.");
        }

        // El security stamp cambia cuando cambia la contraseña: los refresh tokens emitidos antes dejan de servir.
        IdentityUser? user = await users.FindByIdAsync(redeemed.UserId);
        if (user is null || user.SecurityStamp != redeemed.SecurityStamp || await users.IsLockedOutAsync(user))
        {
            await refreshTokens.RevokeFamilyAsync(redeemed.FamilyId, cancellationToken);
            return InvalidCredentials("El refresh token no es válido.");
        }

        return TypedResults.Ok(await tokens.IssueAsync(user, redeemed.FamilyId, cancellationToken));
    }

    // 204 aunque el token no exista: la respuesta no revela qué tokens son válidos.
    private static async Task<NoContent> RevokeRefreshToken(
        RefreshTokenRequest request,
        RefreshTokenStore refreshTokens,
        CancellationToken cancellationToken)
    {
        await refreshTokens.RevokeAsync(request.RefreshToken, cancellationToken);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult InvalidCredentials(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status401Unauthorized, title: "No autorizado");
}
