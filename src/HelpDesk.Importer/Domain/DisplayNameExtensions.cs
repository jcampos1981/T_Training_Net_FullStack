namespace HelpDesk.Importer.Domain;

public static class DisplayNameExtensions
{
    extension(TicketPriority priority)
    {
        public string DisplayName => priority switch
        {
            TicketPriority.Low => "Baja",
            TicketPriority.Medium => "Media",
            TicketPriority.High => "Alta",
            TicketPriority.Critical => "Crítica",
            _ => priority.ToString()
        };
    }

    extension(SlaState state)
    {
        public string DisplayName => state switch
        {
            SlaState.Closed => "Cerrado",
            SlaState.OnTrack => "En tiempo",
            SlaState.AtRisk => "En riesgo",
            SlaState.Breached => "Vencido",
            _ => state.ToString()
        };
    }
}
