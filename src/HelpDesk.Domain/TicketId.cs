namespace HelpDesk.Domain;

public readonly record struct TicketId(int Value) : IComparable<TicketId>
{
    public int Value { get; } = Value > 0
        ? Value
        : throw new ArgumentOutOfRangeException(nameof(Value), Value, "El Id debe ser mayor que cero.");

    // Los Ids salen de una secuencia creciente: compararlos tiene sentido (y permite paginar "después del Id X").
    public int CompareTo(TicketId other) => Value.CompareTo(other.Value);

    public static bool operator <(TicketId left, TicketId right) => left.CompareTo(right) < 0;

    public static bool operator >(TicketId left, TicketId right) => left.CompareTo(right) > 0;

    public static bool operator <=(TicketId left, TicketId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(TicketId left, TicketId right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"T-{Value:D5}";
}
