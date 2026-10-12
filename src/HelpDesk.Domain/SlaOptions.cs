namespace HelpDesk.Domain;

// Clase simple (POCO): el dominio no depende de IOptions ni de ninguna librería de configuración.
public sealed class SlaOptions
{
    public Dictionary<TicketPriority, double> ResolutionHours { get; set; } = new();

    public double AtRiskThreshold { get; set; } = 0.8;
}
