using HelpDesk.Api.Features.Accounts;

namespace HelpDesk.Api.Features.Comments;

// Recurso anidado: los comentarios siempre se piden a través de su ticket, y heredan sus permisos:
// quien puede leer el ticket puede leer sus comentarios; quien puede editarlo, puede comentarlo.
public static class CommentsFeature
{
    public static IServiceCollection AddCommentsFeature(this IServiceCollection services)
    {
        services.AddScoped<ICommentRepository, EfCommentRepository>();
        services.AddScoped<CommentQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapCommentsFeature(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder comments = app.MapGroup("/api/tickets/{ticketId:int:min(1)}/comments")
            .WithTags("Comments")
            .RequireAuthorization(HelpDeskPolicies.Staff)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ListComments.Map(comments);
        GetComment.Map(comments);
        AddComment.Map(comments);

        return app;
    }
}
