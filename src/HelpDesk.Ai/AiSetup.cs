using System.ComponentModel.DataAnnotations;
using Anthropic;
using HelpDesk.ServiceDefaults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace HelpDesk.Ai;

public enum ChatProvider
{
    // En un contenedor: sin clave, sin costo, y los datos no salen de la máquina. Más lento, y las respuestas son más flojas.
    Ollama,

    // Claude, por su API. Cobra por token, y el texto de los tickets sale de la máquina.
    Anthropic
}

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    // Tiene que coincidir con la columna vector(768) de dbo.TicketEmbeddings.
    public const int EmbeddingDimensions = 768;

    public bool Enabled { get; set; }

    public ChatProvider ChatProvider { get; set; } = ChatProvider.Ollama;

    [Url]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    // Para Anthropic, usa claude-opus-5-5 si no se indica otro.
    public string? ChatModel { get; set; }

    // Solo Ollama: Anthropic no ofrece embeddings. Multilingüe, porque los tickets están en español.
    public string EmbeddingModel { get; set; } = "paraphrase-multilingual";

    // Solo Anthropic. En User Secrets o en un almacén de secretos, nunca en appsettings.
    public string? AnthropicApiKey { get; set; }

    // Limita el costo y el tiempo de cada respuesta. Una sugerencia no necesita más.
    [Range(50, 2000)]
    public int MaxOutputTokens { get; set; } = 400;

    [Range(1, 10)]
    public int SimilarTickets { get; set; } = 3;

    // Distancia del coseno: 0 es idéntico y 2 es opuesto. Más allá de esto, no se llama al modelo: se responde que no hay casos.
    // Sale de medirlo con tests/HelpDesk.AiEval. Al cambiar el modelo de embeddings, las distancias cambian y hay que volver a medir.
    [Range(0.0, 2.0)]
    public double MaxDistance { get; set; } = 0.45;

    // Solo los casos casi tan cercanos como el mejor: un caso que no viene al tema confunde al modelo, y termina mezclando soluciones.
    [Range(0.0, 2.0)]
    public double MaxDistanceFromBest { get; set; } = 0.10;

    // Deja que el modelo pida otra búsqueda. Apagado: al medirlo fue más lento y peor, y gemma3:4b no admite herramientas. Ver docs/adr/0003.
    public bool EnableTools { get; set; }
}

public static class AiSetup
{
    // El mejor de los que se midieron con tests/HelpDesk.AiEval: ver docs/adr/0003.
    private const string DefaultOllamaChatModel = "gemma3:4b";
    private const string DefaultClaudeModel = "claude-opus-5-5";

    public static AiOptions Read(IConfiguration configuration) =>
        configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

    public static bool IsEnabled(IConfiguration configuration) => Read(configuration).Enabled;

    public static IServiceCollection AddHelpDeskChat(this IServiceCollection services, IConfiguration configuration)
    {
        AiOptions options = Read(configuration);

        services.AddChatClient(_ => CreateChatClient(options))
            // Si el modelo pide una herramienta, la ejecuta y le devuelve el resultado. Con un límite: un modelo confundido no da vueltas para siempre.
            .UseFunctionInvocation(configure: invocation => invocation.MaximumIterationsPerRequest = 3)
            // Una traza por llamada, con el modelo y los tokens. Sin el texto: EnableSensitiveData queda apagado.
            .UseOpenTelemetry(sourceName: HelpDeskTelemetry.Name);

        return services;
    }

    public static IServiceCollection AddHelpDeskEmbeddings(this IServiceCollection services, IConfiguration configuration)
    {
        AiOptions options = Read(configuration);

        services.AddEmbeddingGenerator(_ => CreateEmbeddingGenerator(options))
            .UseOpenTelemetry(sourceName: HelpDeskTelemetry.Name);

        return services;
    }

    public static IChatClient CreateChatClient(AiOptions options) => options.ChatProvider switch
    {
        ChatProvider.Anthropic => new AnthropicClient { ApiKey = options.AnthropicApiKey ?? throw new InvalidOperationException("Falta Ai:AnthropicApiKey.") }
            .AsIChatClient(options.ChatModel ?? DefaultClaudeModel),
        ChatProvider.Ollama => new OllamaApiClient(new Uri(options.OllamaEndpoint), options.ChatModel ?? DefaultOllamaChatModel),
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.ChatProvider, "Proveedor desconocido.")
    };

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(AiOptions options) =>
        new OllamaApiClient(new Uri(options.OllamaEndpoint), options.EmbeddingModel);
}
