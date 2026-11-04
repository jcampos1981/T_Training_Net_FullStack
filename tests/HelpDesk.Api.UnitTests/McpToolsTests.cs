using System.Net;
using System.Text;
using System.Text.Json;
using HelpDesk.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace HelpDesk.Api.UnitTests;

public sealed class McpToolsTests
{
    private const string Ticket = """
        { "id": 7, "code": "T-00007", "title": "No imprime, avisar a diego.rojas@cliente.cl", "priority": "Low", "status": "Open",
          "createdAt": "2026-11-04T10:00:00Z", "assignedTo": "pedro", "requestedBy": "camila@acme.local", "slaState": "OnTrack", "slaDeadline": "2026-11-05T10:00:00Z" }
        """;

    private const string Comments = """[ { "author": "camila", "body": "Llámame al +56 9 8765 4321", "createdAt": "2026-11-04T10:05:00Z" } ]""";

    [Fact]
    public async Task ATicket_ComesWithoutPersonalData()
    {
        var api = new FakeApi();
        api.Answer("/api/tickets/7", Ticket);
        api.Answer("/api/tickets/7/comments", Comments);

        string json = JsonSerializer.Serialize(await api.Tools.GetAsync(7, Test.Cancel));

        Assert.Contains("T-00007", json);
        Assert.Contains("dato oculto", json);
        Assert.DoesNotContain("diego.rojas", json);
        Assert.DoesNotContain("8765", json);
        Assert.DoesNotContain("camila@acme.local", json);
    }

    [Fact]
    public async Task TheList_AlsoComesWithoutPersonalData()
    {
        var api = new FakeApi();
        api.Answer("/api/tickets", $$"""{ "items": [{{Ticket}}], "nextAfter": 7 }""");

        TicketPage page = await api.Tools.ListAsync(cancellationToken: Test.Cancel);

        Assert.DoesNotContain("diego.rojas", page.Items[0].Title);
        Assert.Equal(7, page.NextAfter);
    }

    [Theory]
    [InlineData(1000, "pageSize=50")]
    [InlineData(0, "pageSize=1")]
    [InlineData(10, "pageSize=10")]
    public async Task TheAssistant_CannotAskForTooMany(int count, string expected)
    {
        var api = new FakeApi();
        api.Answer("/api/tickets", """{ "items": [], "nextAfter": null }""");

        await api.Tools.ListAsync(count: count, cancellationToken: Test.Cancel);

        Assert.Contains(expected, api.LastQuery);
    }

    [Fact]
    public async Task TheFilters_ReachTheApi_WithoutChangingTheQuery()
    {
        var api = new FakeApi();
        api.Answer("/api/tickets", """{ "items": [], "nextAfter": null }""");

        await api.Tools.ListAsync(status: "Open&assignedTo=otro", priority: "High", after: 40, cancellationToken: Test.Cancel);

        Assert.Contains("status=Open%26assignedTo%3dotro", api.LastQuery);
        Assert.Contains("priority=High", api.LastQuery);
        Assert.Contains("after=40", api.LastQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "no tiene permiso")]
    [InlineData(HttpStatusCode.NotFound, "No existe")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task AnError_IsExplainedToTheAssistant(HttpStatusCode status, string expected)
    {
        var api = new FakeApi();
        api.Fail("/api/tickets/7", status);

        McpException error = await Assert.ThrowsAsync<McpException>(() => api.Tools.GetAsync(7, Test.Cancel));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task ItSignsIn_OnlyOnce()
    {
        var api = new FakeApi();
        api.Answer("/api/tickets", """{ "items": [], "nextAfter": null }""");

        await api.Tools.ListAsync(cancellationToken: Test.Cancel);
        await api.Tools.ListAsync(cancellationToken: Test.Cancel);

        Assert.Equal(1, api.SignIns);
    }

    [Fact]
    public async Task WhenTheTokenExpires_ItSignsInAgain()
    {
        var api = new FakeApi();
        api.Answer("/api/tickets", """{ "items": [], "nextAfter": null }""");
        await api.Tools.ListAsync(cancellationToken: Test.Cancel);

        api.RejectTokenOnce();
        TicketPage page = await api.Tools.ListAsync(cancellationToken: Test.Cancel);

        Assert.Empty(page.Items);
        Assert.Equal(2, api.SignIns);
    }

    [Fact]
    public async Task WithAWrongPassword_ItSaysSo()
    {
        var api = new FakeApi { RejectSignIn = true };

        McpException error = await Assert.ThrowsAsync<McpException>(() => api.Tools.ListAsync(cancellationToken: Test.Cancel));

        Assert.Contains("iniciar sesión", error.Message);
    }

    private sealed class FakeApi : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> answers = [];
        private bool rejectToken;

        public FakeApi()
        {
            var options = Options.Create(new HelpDeskApiOptions { BaseUrl = new Uri("http://api.test"), Email = "laura@helpdesk.local", Password = "clave" });
            Tools = new TicketTools(new HelpDeskApiClient(this, options));
        }

        public TicketTools Tools { get; }

        public bool RejectSignIn { get; init; }

        public int SignIns { get; private set; }

        public string LastQuery { get; private set; } = string.Empty;

        public void Answer(string path, string json) => answers[path] = (HttpStatusCode.OK, json);

        public void Fail(string path, HttpStatusCode status) => answers[path] = (status, string.Empty);

        public void RejectTokenOnce() => rejectToken = true;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/token")
            {
                SignIns++;
                return Respond(RejectSignIn ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, """{ "accessToken": "un-token" }""");
            }

            if (rejectToken)
            {
                rejectToken = false;
                return Respond(HttpStatusCode.Unauthorized, string.Empty);
            }

            LastQuery = request.RequestUri.Query;
            (HttpStatusCode status, string body) = answers[path];

            return Respond(status, body);
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
