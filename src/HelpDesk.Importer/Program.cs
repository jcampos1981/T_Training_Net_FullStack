using System.Text;
using HelpDesk.Importer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

Console.OutputEncoding = Encoding.UTF8;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Herramienta de consola: appsettings.json y samples/ se buscan junto al ejecutable,
    // no en la carpeta desde la que se lanza.
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--as-of"] = "Report:AsOf",
    ["--folder"] = "Import:Folder"
});

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// Logs a stderr y reporte a stdout: el reporte se puede redirigir a un archivo sin mezclarse con el log.
builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddHelpDeskImporter();

using IHost host = builder.Build();

try
{
    // Arrancar el host valida la configuración (ValidateOnStart) y conecta Ctrl+C con ApplicationStopping.
    await host.StartAsync();
}
catch (OptionsValidationException exception)
{
    ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HelpDesk.Importer");
    logger.LogError("Configuración inválida: {Failures}", string.Join(" ", exception.Failures));
    return ExitCodes.InvalidConfiguration;
}

IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
int exitCode = await host.Services.GetRequiredService<ImportApp>().RunAsync(lifetime.ApplicationStopping);

await host.StopAsync();
return exitCode;
