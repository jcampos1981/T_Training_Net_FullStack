using System.Net;
using System.Net.Http.Headers;
using HelpDesk.Api.Features.Accounts.Tokens;
using HelpDesk.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace HelpDesk.Api.IntegrationTests;

// En la colección de los barridos: cada instancia nueva de la API hace uno al arrancar.
[Collection(SequentialCollection.Name)]
public sealed class RateLimitTests(HelpDeskApiFactory api)
{
    [Fact]
    public async Task User_OverTheLimit_IsTold429WithRetryAfter()
    {
        await using HelpDeskApiInstance limited = LimitedTo(userBurst: 3, loginAttempts: 1000);
        limited.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await api.TokenOfAsync("jorge"));
        var jorge = new ApiUser(limited.Client);

        HttpResponseMessage[] responses = [await jorge.GetAsync("/api/me"), await jorge.GetAsync("/api/me"), await jorge.GetAsync("/api/me"), await jorge.GetAsync("/api/me")];

        responses[2].ShouldBe(HttpStatusCode.OK);
        responses[3].ShouldBe(HttpStatusCode.TooManyRequests);
        Assert.NotNull(responses[3].Headers.RetryAfter);
    }

    [Fact]
    public async Task Login_OverTheLimit_IsRejected()
    {
        await using HelpDeskApiInstance limited = LimitedTo(userBurst: 1000, loginAttempts: 2);
        var anonymous = new ApiUser(limited.Client);
        var wrong = new TokenRequest("carlos@helpdesk.local", "no-es-la-clave");

        (await anonymous.PostAsync("/api/token", wrong)).ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/token", wrong)).ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/token", wrong)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    // Un 429 a un orquestador le haría creer que la instancia está caída.
    [Fact]
    public async Task Health_IsNotLimited()
    {
        await using HelpDeskApiInstance limited = LimitedTo(userBurst: 3, loginAttempts: 1000);
        var anonymous = new ApiUser(limited.Client);

        for (int request = 0; request < 20; request++)
        {
            (await anonymous.GetAsync("/health/live")).ShouldBe(HttpStatusCode.OK);
        }
    }

    private HelpDeskApiInstance LimitedTo(int userBurst, int loginAttempts) =>
        new(api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:UserBurst", userBurst.ToString());
            builder.UseSetting("RateLimiting:UserRequestsPerMinute", "6");
            builder.UseSetting("RateLimiting:LoginAttemptsPerMinute", loginAttempts.ToString());
        }));
}
