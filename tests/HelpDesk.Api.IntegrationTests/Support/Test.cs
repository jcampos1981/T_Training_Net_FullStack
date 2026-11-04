using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelpDesk.Api.IntegrationTests.Support;

internal static class Test
{
    // xUnit la cancela si la prueba se aborta: nada queda esperando a la base o a un aviso.
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Un título distinto por prueba: la base se comparte entre todas, y así cada una reconoce sus datos.
    public static string UniqueTitle(string label) => $"{label} {Guid.NewGuid():N}";

    // Un agente que solo existe para una prueba: su carga no se mezcla con la de otras.
    public static string UniqueAgent() => $"agente-{Guid.NewGuid():N}"[..20];
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class TicketTimes
{
    // Una fecha de creación que deja a un ticket crítico (4 h de plazo) con este porcentaje del plazo ya consumido.
    public static DateTimeOffset CriticalAt(double fractionOfDeadline) =>
        DateTimeOffset.UtcNow - TimeSpan.FromHours(4 * fractionOfDeadline);
}
