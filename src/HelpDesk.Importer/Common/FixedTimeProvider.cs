namespace HelpDesk.Importer.Common;

// Reloj detenido en un instante: permite reproducir un reporte "a una fecha" dada.
public sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant.ToUniversalTime();
}
