namespace HelpDesk.Domain;

// Se lanza cuando una operación viola una regla de negocio (no un error de programación).
// La API la traduce a 409 Conflict; el mensaje está pensado para el usuario.
public sealed class DomainRuleException(string message) : Exception(message);
