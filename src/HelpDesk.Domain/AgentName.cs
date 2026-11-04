namespace HelpDesk.Domain;

// Value object: no tiene identidad propia, vale por lo que contiene. "Pedro" y "pedro" son el mismo agente, y esa regla vive
// aquí en vez de repetir string.Equals(..., OrdinalIgnoreCase) en cada comparación, donde es fácil olvidarla.
public readonly record struct AgentName
{
    public AgentName(string value) => Value = DomainGuard.NotBlank(value, nameof(value), "El nombre del agente no puede estar vacío.");

    public string Value { get; }

    // Para lo que llega de afuera, donde "sin agente" es null o texto vacío.
    public static AgentName? From(string? value) => string.IsNullOrWhiteSpace(value) ? null : new AgentName(value);

    public bool Equals(AgentName other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override int GetHashCode() => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;
}
