using HelpDesk.Admin.Api;
using HelpDesk.Admin.Authentication;
using HelpDesk.Admin.Components;
using HelpDesk.Admin.Security;
using MudBlazor;
using MudBlazor.Services;

// Para probarlo:
//   docker compose -f deploy/compose.yaml up -d keycloak sqlserver
//   dotnet run --project src/HelpDesk.Api --launch-profile http
//   dotnet run --project src/HelpDesk.Admin        → https://localhost:7300 (pedro: agente, laura: supervisora)

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// Los avisos abajo: arriba a la derecha, tapan "Cerrar sesión" durante varios segundos.
builder.Services.AddMudServices(options => options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight);
builder.Services.AddAdminAuthentication(); // login con Keycloak, cookie y tokens en el servidor
builder.Services.AddHelpDeskApi();         // cliente generado de la API, con el token del usuario de cada circuito

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseSecurityHeaders();

// El panel es solo en español: fechas y números con formato de Chile, sin importar la configuración del servidor.
app.UseRequestLocalization("es-CL");

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();

// Cerrado por defecto: toda página (y el WebSocket del circuito) exige ser del equipo. Una página anónima tiene que
// declararlo con [AllowAnonymous]. La API revisa de nuevo en cada llamada: esto solo decide quién entra al panel.
// ContentSecurityFrameAncestorsPolicy = null: Blazor agregaría su propio "frame-ancestors 'self'" (protege el WebSocket
// comprimido de ataques desde un iframe), pero UseSecurityHeaders ya manda uno más estricto: 'none'.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)
    .RequireAuthorization(AdminAuthentication.StaffPolicy);

app.Run();
