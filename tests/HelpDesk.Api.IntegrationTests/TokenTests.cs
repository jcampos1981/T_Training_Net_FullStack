using System.Net;
using HelpDesk.Api.Features.Accounts.Tokens;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests;

public sealed class TokenTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task WrongPassword_IsRejected() =>
        (await api.Anonymous().PostAsync("/api/token", new TokenRequest("pedro@helpdesk.local", "no-es-la-clave"))).ShouldBe(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task RefreshToken_Rotates_AndReusingOneRevokesTheFamily()
    {
        TokenResponse login = await api.LoginAsync("ana");
        ApiUser anonymous = api.Anonymous();

        TokenResponse renewed = await (await anonymous.PostAsync("/api/token/refresh", new RefreshTokenRequest(login.RefreshToken))).ReadAsync<TokenResponse>();
        Assert.NotEqual(login.RefreshToken, renewed.RefreshToken);

        // Alguien usa el anterior: hay una copia en otra parte. Ni la copia ni el token vigente deben servir.
        (await anonymous.PostAsync("/api/token/refresh", new RefreshTokenRequest(login.RefreshToken))).ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/token/refresh", new RefreshTokenRequest(renewed.RefreshToken))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_EndsTheSession()
    {
        TokenResponse login = await api.LoginAsync("sofia");
        ApiUser anonymous = api.Anonymous();

        (await anonymous.PostAsync("/api/token/revoke", new RefreshTokenRequest(login.RefreshToken))).ShouldBe(HttpStatusCode.NoContent);

        (await anonymous.PostAsync("/api/token/refresh", new RefreshTokenRequest(login.RefreshToken))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyExpiredTokens()
    {
        TokenResponse login = await api.LoginAsync("elena");

        await using AsyncServiceScope scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RefreshTokenStore>().DeleteExpiredAsync(Test.Cancel);

        // El token vigente sobrevivió: todavía se puede renovar.
        await (await api.Anonymous().PostAsync("/api/token/refresh", new RefreshTokenRequest(login.RefreshToken))).ReadAsync<TokenResponse>();
    }
}
