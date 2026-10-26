using System.Text.Json;
using System.Text.Json.Serialization;
using HelpDesk.Api.Data;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Agents;
using HelpDesk.Api.Features.Comments;
using HelpDesk.Api.Features.Dashboard;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Middleware;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Servicios (contenedor de DI): igual que en el Generic Host del importador ----
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();                             // errores con formato estándar RFC 9457
builder.Services.AddExceptionHandler<KnownExceptionHandler>();    // excepciones conocidas → 400 / 409
builder.Services.AddOptions<ApiErrorOptions>().BindConfiguration(ApiErrorOptions.SectionName);
// Por defecto solo Development lanza excepción ante parámetros inválidos; así ambos ambientes pasan
// por KnownExceptionHandler y el formato del error es el mismo (solo cambia el nivel de detalle).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddValidation();                                 // valida [Required], [StringLength]... (.NET 10)

// Minimal APIs y Controllers tienen configuraciones JSON separadas: las dos usan la misma función.
builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.AddControllers().AddJsonOptions(options => ConfigureJson(options.JsonSerializerOptions));

builder.Services.AddSingleton(TimeProvider.System);

// En Development la cadena viene de User Secrets; en otros ambientes, de variables de entorno.
string connectionString = builder.Configuration.GetConnectionString("HelpDesk")
    ?? throw new InvalidOperationException(
        "Falta ConnectionStrings:HelpDesk. En desarrollo: dotnet user-secrets set \"ConnectionStrings:HelpDesk\" \"...\"");

builder.Services.AddSingleton<AuditInterceptor>();
builder.Services.AddDbContext<HelpDeskDbContext>((provider, options) =>
{
    // Reintenta los errores transitorios (víctima de deadlock 1205, corte de conexión, failover en la nube):
    // hasta 6 veces, con esperas crecientes. Las transacciones propias van dentro de CreateExecutionStrategy().
    options.UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure());
    options.AddInterceptors(provider.GetRequiredService<AuditInterceptor>()); // UpdatedAt automático

    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging(); // el log de SQL muestra los valores de los parámetros
    }
});

builder.Services.AddTicketsFeature();
builder.Services.AddCommentsFeature();
builder.Services.AddAgentsFeature();
builder.Services.AddDashboardFeature();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.SeedDevelopmentDataAsync(); // tickets de ejemplo si la tabla está vacía
}

// ---- Pipeline HTTP: cada middleware envuelve a los que vienen después, así que el orden importa ----
app.UseMiddleware<RequestTimingMiddleware>(); // 1º: mide todo y ve el código de estado final
app.UseExceptionHandler();                    // 2º: convierte excepciones en respuestas ProblemDetails
app.UseStatusCodePages();                     // 3º: da cuerpo ProblemDetails a errores vacíos (404, 405...)

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // documento OpenAPI en /openapi/v1.json
    app.MapScalarApiReference(); // interfaz para explorar y probar la API en /scalar
}

app.MapTicketsFeature();  // Minimal APIs
app.MapCommentsFeature(); // Minimal APIs (recurso anidado de tickets)
app.MapDashboardFeature(); // Minimal API con Dapper
app.MapControllers();     // Controllers (AgentsController)

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.Converters.Add(new JsonStringEnumConverter()); // "High" en vez de 2
    options.RespectRequiredConstructorParameters = true;   // falta "priority" → 400
    options.RespectNullableAnnotations = true;             // "title": null → 400
}
