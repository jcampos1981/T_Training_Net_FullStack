using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using HelpDesk.Api.Features.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.IntegrationTests.Support;

// Un cliente conectado al hub, que anota todo lo que recibe.
public sealed class HubListener : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<object> received = new();
    private readonly HubConnection connection;

    private HubListener(HubConnection connection)
    {
        this.connection = connection;

        connection.On<TicketEvent>(nameof(ITicketEventsClient.TicketChanged), received.Enqueue);
        connection.On<PortalTicketEvent>(nameof(ITicketEventsClient.MyTicketChanged), received.Enqueue);
        connection.On<TicketsReassignedEvent>(nameof(ITicketEventsClient.TicketsReassigned), received.Enqueue);
        connection.On<SlaAlertsEvent>(nameof(ITicketEventsClient.SlaAlertsRaised), received.Enqueue);
    }

    public static async Task<HubListener> ConnectAsync(HelpDeskApiFactory api, string user)
    {
        string token = await api.TokenOfAsync(user);

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/tickets"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellation) =>
                {
                    WebSocketClient client = api.Server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                    return await client.ConnectAsync(context.Uri, cancellation);
                };
            })
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        var listener = new HubListener(connection);
        await connection.StartAsync(Test.Cancel);
        await listener.WaitUntilInGroupsAsync();

        return listener;
    }

    public async Task<T> WaitForAsync<T>(Func<T, bool> matches)
    {
        DateTimeOffset limit = DateTimeOffset.UtcNow + WaitLimit;

        while (DateTimeOffset.UtcNow < limit)
        {
            if (received.OfType<T>().FirstOrDefault(matches) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), Test.Cancel);
        }

        Assert.Fail($"No llegó el aviso {typeof(T).Name} esperado en {WaitLimit.TotalSeconds} s.");
        throw new UnreachableException();
    }

    public IEnumerable<T> Received<T>() => received.OfType<T>();

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    // StartAsync vuelve cuando termina el saludo, y el hub une la conexión a sus grupos justo después. El hub no tiene métodos
    // públicos: invocar uno falla, pero esa respuesta llega recién cuando terminó OnConnectedAsync.
    private async Task WaitUntilInGroupsAsync()
    {
        try
        {
            await connection.InvokeAsync("Listo", Test.Cancel);
        }
        catch (HubException)
        {
            // Es lo esperado.
        }
    }
}
