using System.ComponentModel.DataAnnotations;
using HelpDesk.Admin.Api.Generated;
using HelpDesk.Admin.Authentication;
using HelpDesk.Admin.Realtime;
using HelpDesk.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace HelpDesk.Admin.Api;

public sealed class HelpDeskApiOptions
{
    public const string SectionName = "HelpDeskApi";

    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;
}

// El cliente de la API lo genera Kiota desde /openapi/v1.json, como openapi-typescript en el portal React: si la API
// cambia un campo y se regenera, el error aparece al compilar. Los archivos están en Api/Generated y no se editan.
// Para regenerar, con la API corriendo:   dotnet kiota update --output src/HelpDesk.Admin/Api/Generated
public static class HelpDeskApiSetup
{
    public const string HttpClientName = "helpdesk-api";

    public static IServiceCollection AddHelpDeskApi(this IServiceCollection services)
    {
        services.AddOptions<HelpDeskApiOptions>()
            .BindConfiguration(HelpDeskApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName).AddHelpDeskResilience();
        services.AddScoped<IAccessTokenProvider, CircuitAccessTokenProvider>();

        // Scoped = un cliente por circuito, con el token de quien está conectado a ese circuito.
        services.AddScoped(provider =>
        {
            HttpClient http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var authentication = new BaseBearerTokenAuthenticationProvider(provider.GetRequiredService<IAccessTokenProvider>());
            var adapter = new HttpClientRequestAdapter(authentication, httpClient: http)
            {
                BaseUrl = provider.GetRequiredService<IOptions<HelpDeskApiOptions>>().Value.BaseUrl
            };

            return new HelpDeskApiClient(adapter);
        });

        services.AddScoped<TicketEventsConnection>();

        services.AddOptions<SuggestionsOptions>().BindConfiguration(SuggestionsOptions.SectionName);
        services.AddScoped<SuggestionsClient>();

        return services;
    }
}
