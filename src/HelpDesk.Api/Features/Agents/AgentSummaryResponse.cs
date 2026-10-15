namespace HelpDesk.Api.Features.Agents;

public sealed record AgentSummaryResponse(string Name, int OpenTickets, int TotalTickets);
