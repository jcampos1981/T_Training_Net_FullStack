namespace HelpDesk.Importer.Domain;

public readonly record struct TicketId(int Value)
{
    public int Value { get; } = Value > 0
        ? Value
        : throw new ArgumentOutOfRangeException(nameof(Value), Value, "El Id debe ser mayor que cero.");

    public override string ToString() => $"T-{Value:D5}";
}
