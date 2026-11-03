using HelpDesk.Bff.Authentication;
using HelpDesk.Bff.Endpoints;
using HelpDesk.Bff.Frontend;
using HelpDesk.Bff.Proxy;
using Microsoft.AspNetCore.HttpOverrides;

// Para probarlo:
//   docker compose -f deploy/compose.yaml up -d keycloak sqlserver redis
//   dotnet run --project src/HelpDesk.Api --launch-profile http
//   dotnet run --project src/HelpDesk.Bff          → https://localhost:7200 (usuarios del realm helpdesk)
// y el portal React, de una de dos formas (en src/HelpDesk.Portal):
//   npm run dev     → http://localhost:5173, con recarga en caliente; Vite reenvía /api y /bff a este BFF
//   npm run build   → compila a wwwroot, y este BFF lo sirve en https://localhost:7200 (como en producción)

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// Detrás de un proxy (el de Vite en desarrollo, un balanceador en producción) la conexión llega desde el proxy y
// por https. Estos headers dicen el esquema y la IP originales del navegador, y con ellos el BFF arma bien la
// redirect_uri de Keycloak. Solo se aceptan de proxies de confianza: por defecto, los de esta misma máquina.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddBffAuthentication(); // login con Keycloak y sesión en una cookie
builder.Services.AddAuthorization();
builder.Services.AddApiProxy(builder.Configuration); // /api/... y /hubs/... → HelpDesk.Api, con el access token

WebApplication app = builder.Build();

app.UseForwardedHeaders(); // primero: todo lo que sigue (OpenID Connect, cookies) debe ver el esquema real
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseFrontendSecurityHeaders(); // Content-Security-Policy y nosniff en todas las respuestas
app.UseFrontendFiles();           // los archivos del portal (wwwroot), con la caché que corresponde a cada uno

app.UseAuthentication();
app.UseAuthorization();

app.MapBffEndpoints();     // /bff/login, /bff/user, /bff/logout
app.MapApiProxy();         // /api/... y /hubs/...
app.MapFrontendFallback(); // todo lo demás es una pantalla de React: index.html

app.Run();
