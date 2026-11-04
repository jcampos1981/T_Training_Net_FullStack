using HelpDesk.Ai;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HelpDesk.Api.Features.Suggestions;

public static class SuggestionsFeature
{
    public static IServiceCollection AddSuggestionsFeature(this IServiceCollection services, IConfiguration configuration)
    {
        // Sin un modelo configurado, la API funciona igual, y sin esta función. El mediador registra todos los handlers que encuentra, y este no se puede
        // construir sin modelo: el contenedor lo revisa al arrancar, y la API no arrancaría.
        if (!AiSetup.IsEnabled(configuration))
        {
            services.RemoveAll<IRequestHandler<SuggestReply.Command, Result<SuggestReply.Suggestion>>>();
            return services;
        }

        services.AddOptions<AiOptions>().BindConfiguration(AiOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddHelpDeskChat(configuration);
        services.AddHelpDeskEmbeddings(configuration);
        services.AddScoped<SimilarTicketSearch>();

        return services;
    }
}
