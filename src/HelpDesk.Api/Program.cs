using System.Text.Json.Serialization;
using HelpDesk.Api.Errors;
using HelpDesk.Api.Features.Tickets;
using HelpDesk.Api.Middleware;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Servicios (contenedor de DI): igual que en el Generic Host del importador ----
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();                             // errores con formato estándar RFC 9457
builder.Services.AddExceptionHandler<KnownExceptionHandler>();    // excepciones conocidas → 400 / 409
builder.Services.AddValidation();                                 // valida [Required], [StringLength]... (.NET 10)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); // "High" en vez de 2
    options.SerializerOptions.RespectRequiredConstructorParameters = true;   // falta "priority" → 400
    options.SerializerOptions.RespectNullableAnnotations = true;             // "title": null → 400
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTicketsFeature();

WebApplication app = builder.Build();

// ---- Pipeline HTTP: cada middleware envuelve a los que vienen después, así que el orden importa ----
app.UseMiddleware<RequestTimingMiddleware>(); // 1º: mide todo y ve el código de estado final
app.UseExceptionHandler();                    // 2º: convierte excepciones en respuestas ProblemDetails
app.UseStatusCodePages();                     // 3º: da cuerpo ProblemDetails a errores vacíos (404, 405...)

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();            // documento OpenAPI en /openapi/v1.json
    app.MapScalarApiReference(); // interfaz para explorar y probar la API en /scalar
}

app.MapTicketsFeature();

app.Run();
