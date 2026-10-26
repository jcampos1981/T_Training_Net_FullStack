namespace HelpDesk.Api.Features.Dashboard;

public sealed record AgentWorkloadRow(string Agent, int OpenTickets, int CriticalTickets, int OldestOpenHours);
