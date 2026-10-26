namespace HelpDesk.Api.Features.Agents;

public sealed record ReassignTicketsResponse(string From, string To, int ReassignedTickets);
