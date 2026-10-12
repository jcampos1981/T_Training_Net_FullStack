namespace HelpDesk.Importer.Reports;

public sealed record AgentWorkload(string Agent, int OpenTickets, int BreachedTickets);
