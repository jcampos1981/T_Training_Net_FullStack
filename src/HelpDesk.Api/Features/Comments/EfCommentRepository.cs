using HelpDesk.Api.Data;
using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Comments;

public sealed class EfCommentRepository(HelpDeskDbContext db) : ICommentRepository
{
    public async Task<TicketComment> AddAsync(TicketComment comment, CancellationToken cancellationToken)
    {
        db.Comments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        // Tras el INSERT, EF escribió en el objeto el Id generado por el IDENTITY.
        return comment;
    }
}
