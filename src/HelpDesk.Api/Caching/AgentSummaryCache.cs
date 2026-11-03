namespace HelpDesk.Api.Caching;

// El resumen de GET /api/agents, igual para todo el que puede verlo. Cuesta 7 mil lecturas y 230 ms de CPU por consulta.
public static class AgentSummaryCache
{
    public const string Policy = "agent-summary";

    // Para vaciarla apenas alguien reasigna: esa persona espera ver el resultado de inmediato.
    public const string Tag = "agent-summary";

    // Menor que la espera del panel antes de recargar (Agents.razor), para que esa recarga traiga datos nuevos.
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);
}
