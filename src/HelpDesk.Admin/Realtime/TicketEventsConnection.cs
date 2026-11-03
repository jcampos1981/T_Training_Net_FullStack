using System.Text.Json.Serialization;
using HelpDesk.Admin.Api;
using HelpDesk.Admin.Authentication;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;

namespace HelpDesk.Admin.Realtime;

// La conexión del panel con el hub de avisos de la API. Scoped: una por circuito, con el token del usuario de ese
// circuito. Quedan dos SignalR en cadena: el navegador habla con este servidor (el circuito), y este servidor con la API.
//
// Ojo: los eventos llegan en un hilo de SignalR, no en el del circuito. Un componente que los escuche debe volver a su
// hilo con InvokeAsync antes de tocar su estado, y dejar de escuchar en Dispose: este objeto vive más que una pantalla.
public sealed class TicketEventsConnection : IAsyncDisposable
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // Una conexión que no llegó a durar esto cuenta como un intento fallido: el servidor la aceptó y la cerró enseguida. Pasa
    // con Redis caído, porque no puede unirla a sus grupos. Sin esta regla, se reintentaría al instante, en bucle.
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);

    private readonly HubConnection connection;
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeProvider clock;
    private readonly ILogger<TicketEventsConnection> logger;
    private int started;
    private int failures;
    private DateTimeOffset connectedAt;

    public TicketEventsConnection(
        IAccessTokenProvider accessTokens,
        IOptions<HelpDeskApiOptions> options,
        TimeProvider clock,
        ILogger<TicketEventsConnection> logger)
    {
        this.clock = clock;
        this.logger = logger;

        var hubUrl = new Uri(new Uri(options.Value.BaseUrl), "/hubs/tickets");

        connection = new HubConnectionBuilder()
            // Se pide en cada conexión: si el token vigente está por vencer, SessionAccessTokens lo renueva.
            .WithUrl(hubUrl, http => http.AccessTokenProvider =
                async () => await accessTokens.GetAuthorizationTokenAsync(hubUrl, cancellationToken: stopping.Token))
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        connection.On<TicketEvent>("TicketChanged", ticketEvent =>
        {
            TicketChanged?.Invoke(ticketEvent);
            TicketsChanged?.Invoke(ticketEvent.TicketId);
        });

        connection.On<TicketsReassignedEvent>("TicketsReassigned", reassigned =>
        {
            TicketsReassigned?.Invoke(reassigned);
            TicketsChanged?.Invoke(null);
        });

        // Una alerta no cambia el ticket, pero sí su estado de SLA: las pantallas que lo muestran deben enterarse.
        connection.On<SlaAlertsEvent>("SlaAlertsRaised", alerts =>
        {
            SlaAlertsRaised?.Invoke(alerts);
            TicketsChanged?.Invoke(alerts.Ticket?.Id);
        });

        connection.Closed += HandleClosedAsync;
    }

    public event Action<TicketEvent>? TicketChanged;

    public event Action<TicketsReassignedEvent>? TicketsReassigned;

    public event Action<SlaAlertsEvent>? SlaAlertsRaised;

    // Para las pantallas: qué ticket cambió, o null si pueden ser varios (reasignación masiva, o se recuperó la conexión).
    public event Action<int?>? TicketsChanged;

    public event Action? LiveStateChanged;

    public bool IsLive { get; private set; } = true;

    // La llama el layout al abrir el circuito. No espera a conectarse: sin avisos en vivo, el panel funciona igual.
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            _ = KeepConnectedAsync(reconnecting: false);
        }
    }

    private Task HandleClosedAsync(Exception? error)
    {
        if (!stopping.IsCancellationRequested)
        {
            failures = clock.GetUtcNow() - connectedAt < StableConnection ? failures + 1 : 0;
            _ = KeepConnectedAsync(reconnecting: true);
        }

        return Task.CompletedTask;
    }

    // Un solo camino para las tres situaciones: el primer intento, un corte de red y el cierre al vencer el token.
    // WithAutomaticReconnect() no cubre el primer intento, y deja de intentar después de cuatro veces.
    private async Task KeepConnectedAsync(bool reconnecting)
    {
        try
        {
            while (true)
            {
                // Solo un intento fallido cambia el estado: el cierre rutinario al vencer el token no se nota en pantalla.
                if (failures > 0)
                {
                    SetLive(false);
                    await Task.Delay(RetryDelay(failures - 1), stopping.Token);
                }

                try
                {
                    await connection.StartAsync(stopping.Token);
                    break;
                }
                catch (Exception exception) when (exception is not SessionEndedException && !stopping.IsCancellationRequested)
                {
                    failures++;
                    reconnecting = true;
                    logger.LogInformation("Sin conexión al hub de avisos (intento {Attempt}): {Error}", failures, exception.Message);
                }
            }

            connectedAt = clock.GetUtcNow();
            SetLive(true);

            // SignalR no guarda lo que se envió durante el corte: las pantallas deben volver a preguntar.
            if (reconnecting)
            {
                TicketsChanged?.Invoke(null);
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // El circuito terminó mientras se conectaba: nadie espera esta conexión.
        }
        catch (SessionEndedException)
        {
            // La próxima llamada a la API lleva al login (ApiErrorBoundary). Sin sesión no hay nada que reintentar.
            logger.LogInformation("La sesión terminó: se deja de intentar la conexión al hub de avisos.");
        }
    }

    private void SetLive(bool isLive)
    {
        if (IsLive != isLive)
        {
            IsLive = isLive;
            LiveStateChanged?.Invoke();
        }
    }

    // Espera cada vez más entre intentos, y con azar: si la API se reinicia, los circuitos no vuelven todos a la vez.
    private static TimeSpan RetryDelay(int attempt)
    {
        TimeSpan delay = TimeSpan.FromSeconds(Math.Min(MaxRetryDelay.TotalSeconds, Math.Pow(2, attempt)));
        return delay / 2 + delay / 2 * Random.Shared.NextDouble();
    }

    // Blazor la llama cuando termina el circuito, porque el contenedor libera los servicios Scoped.
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await connection.DisposeAsync();
        stopping.Dispose();
    }
}
