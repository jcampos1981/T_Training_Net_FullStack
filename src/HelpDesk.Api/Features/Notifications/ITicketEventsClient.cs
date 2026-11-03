namespace HelpDesk.Api.Features.Notifications;

// Hub tipado: lo que el servidor puede invocar en los clientes. Sin esto serían textos ("TicketChanged") que recién
// fallan en ejecución si alguien se equivoca al escribirlos. El nombre del método es el que escuchan React y Blazor.
public interface ITicketEventsClient
{
    Task TicketChanged(TicketEvent ticketEvent);

    Task MyTicketChanged(PortalTicketEvent ticketEvent);

    Task TicketsReassigned(TicketsReassignedEvent reassigned);
}
