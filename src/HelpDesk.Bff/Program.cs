using HelpDesk.Bff.Authentication;
using HelpDesk.Bff.Endpoints;
using HelpDesk.Bff.Proxy;
using Microsoft.AspNetCore.HttpOverrides;

// Para probarlo:
//   docker compose -f deploy/compose.yaml up -d keycloak
//   dotnet run --project src/HelpDesk.Api --launch-profile http
//   dotnet run --project src/HelpDesk.Bff          → https://localhost:7200 (usuarios del realm helpdesk)
//   npm run dev  (en src/HelpDesk.Portal)          → http://localhost:5173, el portal React a través del BFF

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// Detrás de un proxy (el de Vite en desarrollo, un balanceador en producción) la conexión llega desde el proxy y
// por https. Estos headers dicen el esquema y la IP originales del navegador, y con ellos el BFF arma bien la
// redirect_uri de Keycloak. Solo se aceptan de proxies de confianza: por defecto, los de esta misma máquina.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddBffAuthentication(); // login con Keycloak y sesión en una cookie
builder.Services.AddAuthorization();
builder.Services.AddApiProxy(builder.Configuration); // /api/... → HelpDesk.Api, con el access token

WebApplication app = builder.Build();

app.UseForwardedHeaders(); // primero: todo lo que sigue (OpenID Connect, cookies) debe ver el esquema real
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
