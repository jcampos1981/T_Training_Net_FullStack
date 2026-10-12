using System.Text.Json.Serialization;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Middleware;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Servicios (contenedor de DI): igual que en el Generic Host del importador ----
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())); // "High" en vez de 2
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTicketsFeature();

WebApplication app = builder.Build();

// ---- Pipeline HTTP: cada middleware envuelve a los que vienen después, así que el orden importa ----
app.UseMiddleware<RequestTimingMiddleware>(); // primero: así mide todo lo que viene detrás

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // documento OpenAPI en /openapi/v1.json
    app.MapScalarApiReference(); // interfaz para explorar y probar la API en /scalar
}

app.MapTicketsFeature();

app.Run();
