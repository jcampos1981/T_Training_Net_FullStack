using HelpDesk.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Un servidor MCP: el asistente lo arranca como un proceso, y conversan por stdin y stdout.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// La cuenta con la que entra. Fuera del repositorio y de la configuración del asistente.
builder.Configuration.AddUserSecrets<HelpDeskApiOptions>(optional: true);

// Lo que sale por stdout es el protocolo: un log ahí rompe la conversación.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddOptions<HelpDeskApiOptions>()
    .BindConfiguration(HelpDeskApiOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<HelpDeskApiClient>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<TicketTools>();

await builder.Build().RunAsync();
