using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace HelpDesk.Mcp;

public sealed class HelpDeskApiOptions
{
    public const string SectionName = "HelpDeskApi";

    [Required]
    public Uri BaseUrl { get; set; } = new("http://localhost:5159");

    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

// Pasa por la API, y no por la base de datos, con la cuenta de una persona: el asistente ve lo que esa persona puede ver, ni un ticket más.
public sealed class HelpDeskApiClient(IHttpClientFactory clients, IOptions<HelpDeskApiOptions> options)
{
    private sealed record TokenResponse(string AccessToken);

    private string? accessToken;

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpClient http = clients.CreateClient();
        http.BaseAddress = options.Value.BaseUrl;

        accessToken ??= await SignInAsync(http, cancellationToken);
        HttpResponseMessage response = await SendAsync(http, path, cancellationToken);

        // El token dura una hora, y este proceso puede vivir días.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            accessToken = await SignInAsync(http, cancellationToken);
            response = await SendAsync(http, path, cancellationToken);
        }

        using (response)
        {
            // McpException: su mensaje es lo único de un error que llega al asistente.
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new McpException("La API respondió vacío."),
                HttpStatusCode.Forbidden => throw new McpException("Esta cuenta no tiene permiso para ver eso."),
                HttpStatusCode.NotFound => throw new McpException("No existe."),
                _ => throw new McpException($"La API respondió {(int)response.StatusCode}.")
            };
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return http.SendAsync(request, cancellationToken);
    }

    private async Task<string> SignInAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("/api/token", new { options.Value.Email, options.Value.Password }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException("No se pudo iniciar sesión en HelpDesk: revisa la cuenta configurada.");
        }

        TokenResponse token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new McpException("La API no devolvió un token.");

        return token.AccessToken;
    }
}
