using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;

namespace HelpDesk.Admin.Api;

public sealed class SuggestionsOptions
{
    public const string SectionName = "Suggestions";

    // Solo si la API tiene un modelo configurado: sin esto, el botón no aparece.
    public bool Enabled { get; set; }
}

public sealed record SuggestionSource(int TicketId, string Code, string Title, double Similarity);

public abstract record SuggestionEvent
{
    public sealed record Sources(IReadOnlyList<SuggestionSource> Items) : SuggestionEvent;

    public sealed record Text(string Chunk) : SuggestionEvent;

    public sealed record Failed(string Message) : SuggestionEvent;
}

// La sugerencia llega como un stream de eventos, algo que el cliente generado por Kiota no sabe leer. Como el hub, este contrato se mantiene a mano: si la
// API lo cambia, el compilador no se entera. Solo las pruebas de punta a punta lo detectan.
public sealed class SuggestionsClient(
    IHttpClientFactory clients,
    IAccessTokenProvider accessTokens,
    IOptions<HelpDeskApiOptions> options)
{
    private const string UnavailableMessage = "El asistente no está disponible en este momento.";

    public async IAsyncEnumerable<SuggestionEvent> StreamAsync(int ticketId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = new Uri($"{options.Value.BaseUrl.TrimEnd('/')}/api/tickets/{ticketId}/suggested-reply");
        string token = await accessTokens.GetAuthorizationTokenAsync(url, cancellationToken: cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpClient http = clients.CreateClient(HelpDeskApiSetup.HttpClientName);

        // Sin esperar el cuerpo: si no, HttpClient lo guarda completo antes de devolver, y se pierde lo de ir viéndolo a medida que se escribe.
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            yield return new SuggestionEvent.Failed(response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Pediste muchas sugerencias seguidas. Espera un momento."
                : UnavailableMessage);
            yield break;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (SseItem<string> item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            switch (item.EventType)
            {
                case "sources":
                    yield return new SuggestionEvent.Sources(JsonSerializer.Deserialize<SuggestionSource[]>(item.Data, JsonSerializerOptions.Web) ?? []);
                    break;
                case "text":
                    yield return new SuggestionEvent.Text(item.Data);
                    break;
                case "error":
                    yield return new SuggestionEvent.Failed(item.Data);
                    break;
            }
        }
    }
}
