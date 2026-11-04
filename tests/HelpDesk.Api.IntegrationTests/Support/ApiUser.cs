using System.Net;
using System.Net.Http.Json;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Domain;

namespace HelpDesk.Api.IntegrationTests.Support;

// Un usuario hablándole a la API por HTTP, como lo haría cualquier cliente. Con el token de ese usuario, si tiene uno.
public sealed class ApiUser(HttpClient http)
{
    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url, Test.Cancel);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) =>
        http.PostAsJsonAsync(url, body ?? new { }, Json.Options, Test.Cancel);

    public Task<HttpResponseMessage> PutAsync(string url, object body) =>
        http.PutAsJsonAsync(url, body, Json.Options, Test.Cancel);

    public Task<HttpResponseMessage> DeleteAsync(string url) => http.DeleteAsync(url, Test.Cancel);

    public async Task<T> ReadAsync<T>(string url) => await (await GetAsync(url)).ReadAsync<T>();

    public async Task<TicketResponse> CreateTicketAsync(
        string title,
        TicketPriority priority = TicketPriority.Medium,
        string? assignedTo = null)
    {
        HttpResponseMessage response = await PostAsync("/api/tickets", new CreateTicketRequest(title, priority, assignedTo));
        return await response.ReadAsync<TicketResponse>();
    }

    public async Task<PortalTicketResponse> OpenTicketAsync(string title, TicketPriority priority = TicketPriority.Medium)
    {
        HttpResponseMessage response = await PostAsync(
            "/api/portal/tickets", new OpenTicketRequest(title, "Descripción de la prueba.", priority));
        return await response.ReadAsync<PortalTicketResponse>();
    }

    public async Task<CommentResponse> CommentAsync(int ticketId, string body = "Comentario de prueba")
    {
        HttpResponseMessage response = await PostAsync($"/api/tickets/{ticketId}/comments", new AddCommentRequest(body));
        return await response.ReadAsync<CommentResponse>();
    }

    // Cambia lo que se indique, y mantiene lo demás tal como está en la base.
    public async Task<HttpResponseMessage> UpdateTicketAsync(
        int ticketId,
        string? title = null,
        TicketPriority? priority = null,
        TicketStatus? status = null,
        string? assignedTo = null,
        string? version = null)
    {
        TicketResponse current = await ReadAsync<TicketResponse>($"/api/tickets/{ticketId}");

        return await PutAsync($"/api/tickets/{ticketId}", new UpdateTicketRequest(
            title ?? current.Title,
            priority ?? current.Priority,
            status ?? current.Status,
            version ?? current.Version,
            assignedTo ?? current.AssignedTo));
    }
}

public static class HttpResponseExtensions
{
    // Si la respuesta no es la esperada, la falla muestra el cuerpo: ahí suele estar la explicación.
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(Test.Cancel);
            Assert.Fail($"Se esperaba una respuesta exitosa y llegó {(int)response.StatusCode}: {body}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, Test.Cancel))!;
    }

    public static void ShouldBe(this HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.Equal(expected, response.StatusCode);

    public static Task<string> TextAsync(this HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(Test.Cancel);
}
