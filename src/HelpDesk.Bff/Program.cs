using HelpDesk.Bff.Authentication;
using HelpDesk.Bff.Endpoints;
using HelpDesk.Bff.Proxy;

// Para probarlo:
//   docker compose -f deploy/compose.yaml up -d keycloak
//   dotnet run --project src/HelpDesk.Api --launch-profile http
//   dotnet run --project src/HelpDesk.Bff          → https://localhost:7200 (usuarios del realm helpdesk)

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddBffAuthentication(); // login con Keycloak y sesión en una cookie
builder.Services.AddAuthorization();
builder.Services.AddApiProxy(builder.Configuration); // /api/... → HelpDesk.Api, con el access token

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// El frontend (por ahora una página de prueba; en la Fase 6, React) se sirve desde el mismo origen que /bff y /api.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapBffEndpoints(); // /bff/login, /bff/user, /bff/logout
app.MapApiProxy();     // /api/...

app.Run();
