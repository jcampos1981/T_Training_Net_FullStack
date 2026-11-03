using Microsoft.AspNetCore.SignalR;

namespace HelpDesk.Api.Features.Notifications;

// Solo avisa: no tiene métodos públicos, así que los clientes escuchan pero no pueden invocar nada. Para cambiar algo
// usan la API, con sus validaciones y permisos. Se crea una instancia por cada conexión o evento: no guarda estado.
public sealed class TicketEventsHub : Hub<ITicketEventsClient>
{
    // SignalR olvida los grupos cuando se corta la conexión. Por eso se arman aquí, en cada conexión y reconexión.
    public override async Task OnConnectedAsync()
    {
        foreach (string group in TicketAudience.GroupsOf(Context.User!))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        await base.OnConnectedAsync();
    }
}
