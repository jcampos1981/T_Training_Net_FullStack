namespace HelpDesk.Api.Features.Dashboard;

public sealed record DashboardResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StatusCount> TicketsByStatus,
    IReadOnlyList<AgentWorkloadRow> OpenWorkload,
    IReadOnlyList<DailyCommentCount> CommentsLast7Days);
