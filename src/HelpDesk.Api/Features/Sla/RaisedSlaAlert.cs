using HelpDesk.Domain;

namespace HelpDesk.Api.Features.Sla;

// Una alerta recién creada, con lo que hace falta para avisar. Dapper la arma por posición: mismo orden que el SELECT.
public sealed record RaisedSlaAlert(int TicketId, SlaState Level, string Title, string? AssignedTo);
