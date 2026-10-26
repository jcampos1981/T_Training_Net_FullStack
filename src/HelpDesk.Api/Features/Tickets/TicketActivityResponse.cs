using HelpDesk.Api.Features.Comments;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Tickets;

public sealed record TicketActivityResponse(
    int Id,
    string Title,
    TicketStatus Status,
    int CommentCount,
    IReadOnlyList<CommentResponse> LatestComments);
