using System.ComponentModel;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using HelpDesk.Ai;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Mediation;
using HelpDesk.Api.Security;
using HelpDesk.Domain;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Features.Suggestions;

public sealed record SuggestionSource(int TicketId, string Code, string Title, double Similarity);

// RAG: se buscan tickets resueltos parecidos, y el modelo redacta a partir de ellos. Es un borrador: no se guarda ni se envía, lo decide un agente.
public static class SuggestReply
{
    private const int CommentsPerCase = 3;

    public sealed record Command(TicketId Id, ClaimsPrincipal User) : IRequest<Result<Suggestion>>;

    public sealed record Suggestion(IReadOnlyList<SuggestionSource> Sources, IAsyncEnumerable<string> Text);

    public sealed class Handler(
        TicketAccess access,
        SimilarTicketSearch search,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IChatClient chat,
        IOptions<AiOptions> options,
        ILogger<Handler> logger) : IRequestHandler<Command, Result<Suggestion>>
    {
        public async Task<Result<Suggestion>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<Ticket> found = await access.FindAsync(command.Id, command.User, TicketOperations.Read, cancellationToken);

            if (!found.IsSuccess)
            {
                return found.Error;
            }

            Ticket ticket = found.Value;
            string? description = await search.DescriptionAsync(ticket.Id, cancellationToken);

            ReadOnlyMemory<float> vector;
            try
            {
                vector = await embeddings.GenerateVectorAsync(ReplyPrompt.SearchTextFor(ticket.Title, description), cancellationToken: cancellationToken);
            }
            // Cualquier falla: cada proveedor lanza sus propias excepciones, y que el modelo falle no es un error de esta aplicación.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "El modelo de embeddings no responde.");
                return Error.Unavailable("El asistente no está disponible en este momento.");
            }

            IReadOnlyList<SimilarTicket> similar = await search.FindAsync(vector, ticket.Id, cancellationToken);
            IReadOnlyDictionary<int, IReadOnlyList<string>> answers = await search.TeamCommentsAsync(similar.Select(item => item.Id), CommentsPerCase, cancellationToken);

            // Un ticket en el que el equipo no escribió nada no dice qué se hizo: no sirve de caso.
            SimilarTicket[] usable = [.. similar.Where(item => answers.ContainsKey(item.Id))];

            if (usable.Length == 0)
            {
                return new Suggestion([], Single(ReplyPrompt.NoMatchAnswer));
            }

            ResolvedCase[] cases = [.. usable.Select(item => new ResolvedCase(answers[item.Id]))];
            SuggestionSource[] sources = [.. usable.Select(item => new SuggestionSource(item.Id, new TicketId(item.Id).ToString(), item.Title, 1 - item.Distance))];

            return new Suggestion(sources, GenerateAsync(ticket, cases, cancellationToken));
        }

        private async IAsyncEnumerable<string> GenerateAsync(
            Ticket ticket,
            IReadOnlyList<ResolvedCase> cases,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ChatOptions chatOptions = ReplyPrompt.ChatOptionsFor(options.Value);

            if (options.Value.EnableTools)
            {
                chatOptions.Tools = [AIFunctionFactory.Create(SearchResolvedTicketsAsync, "buscar_tickets_resueltos")];
            }

            long inputTokens = 0;
            long outputTokens = 0;

            await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync(ReplyPrompt.Build(ticket.Title, cases), chatOptions, cancellationToken))
            {
                foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
                {
                    inputTokens += usage.Details.InputTokenCount ?? 0;
                    outputTokens += usage.Details.OutputTokenCount ?? 0;
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }

            // Lo que cuesta: en un proveedor de pago, cada sugerencia se puede traducir a dinero.
            logger.LogInformation("Sugerencia para {Ticket}: {InputTokens} tokens de entrada y {OutputTokens} de salida.", ticket.Id, inputTokens, outputTokens);
        }

        // Solo lee, y solo tickets resueltos: un texto malicioso que convenza al modelo no puede cambiar nada, ni ver más de lo que este caso de uso ya usa.
        [Description("Busca tickets ya resueltos parecidos a un problema, y devuelve qué se les respondió.")]
        private async Task<string> SearchResolvedTicketsAsync(
            [Description("El problema a buscar, en pocas palabras.")] string problem,
            CancellationToken cancellationToken)
        {
            ReadOnlyMemory<float> vector = await embeddings.GenerateVectorAsync(PersonalData.Mask(problem), cancellationToken: cancellationToken);
            IReadOnlyList<SimilarTicket> similar = await search.FindAsync(vector, default, cancellationToken);
            IReadOnlyDictionary<int, IReadOnlyList<string>> answers = await search.TeamCommentsAsync(similar.Select(item => item.Id), CommentsPerCase, cancellationToken);

            return answers.Count == 0
                ? "No hay casos parecidos."
                : string.Join("\n\n", answers.Values.Select(comments => PersonalData.Mask(string.Join("\n", comments))));
        }

        private static async IAsyncEnumerable<string> Single(string text)
        {
            await Task.CompletedTask;
            yield return text;
        }
    }

    public static void Map(IEndpointRouteBuilder tickets) =>
        tickets.MapPost("/{id:int:min(1)}/suggested-reply", HandleAsync)
            .WithName(nameof(SuggestReply))
            .RequireRateLimiting(RateLimitingSetup.Suggestions)
            .WithRequestTimeout(TimeSpan.FromMinutes(2)) // en CPU, un modelo local tarda bastante más que los 15 segundos de una petición normal
            // Eventos en un stream: el OpenAPI no los describe, igual que al hub. El contrato se mantiene a mano en el panel.
            .ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(int id, ClaimsPrincipal user, ISender sender, ILogger<Handler> logger, CancellationToken cancellationToken) =>
        (await sender.SendAsync(new Command(new TicketId(id), user), cancellationToken))
            .ToHttp(suggestion => TypedResults.ServerSentEvents(EventsOf(suggestion, logger, cancellationToken)));

    // Primero de dónde sale, y después el texto a medida que el modelo lo escribe: en CPU, esperar la respuesta entera serían decenas de segundos en blanco.
    private static async IAsyncEnumerable<SseItem<string>> EventsOf(Suggestion suggestion, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SseItem<string>(JsonSerializer.Serialize(suggestion.Sources, JsonSerializerOptions.Web), SuggestionEvents.Sources);

        await using IAsyncEnumerator<string> text = suggestion.Text.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            string? chunk = null;
            bool failed = false;

            try
            {
                chunk = await text.MoveNextAsync() ? text.Current : null;
            }
            // Cualquier falla, como en la búsqueda. Un modelo que no admite herramientas, por ejemplo, no es un problema de red.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "El modelo falló mientras redactaba una sugerencia.");
                failed = true;
            }

            // Ya se respondió 200 y se empezó a enviar: un error solo se puede avisar dentro del stream.
            if (failed)
            {
                yield return new SseItem<string>("El asistente dejó de responder.", SuggestionEvents.Error);
            }

            if (chunk is null)
            {
                yield break;
            }

            yield return new SseItem<string>(chunk, SuggestionEvents.Text);
        }
    }
}

public static class SuggestionEvents
{
    public const string Sources = "sources";
    public const string Text = "text";
    public const string Error = "error";
}
