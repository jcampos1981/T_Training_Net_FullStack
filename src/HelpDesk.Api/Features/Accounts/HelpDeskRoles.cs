namespace HelpDesk.Api.Features.Accounts;

// Los roles del HelpDesk, como constantes: un nombre mal escrito en un [Authorize] no daría error, solo
// dejaría pasar (o bloquear) a quien no corresponde.
public static class HelpDeskRoles
{
    public const string Agent = "Agent";            // atiende los tickets que tiene asignados
    public const string Supervisor = "Supervisor";  // ve todos los tickets y reasigna la carga
    public const string Admin = "Admin";            // administra usuarios

    public static readonly IReadOnlyList<string> All = [Agent, Supervisor, Admin];
}

// Claims propios. Un claim es un dato del usuario que viaja dentro de su identidad (la cookie o el token):
// la API lo lee sin ir a la base en cada petición.
public static class HelpDeskClaimTypes
{
    // El nombre con el que el agente aparece en Ticket.AssignedTo ("pedro"). El usuario se identifica por
    // su email (pedro@helpdesk.local); este claim une la cuenta con los tickets.
    public const string Agent = "helpdesk:agent";
}
