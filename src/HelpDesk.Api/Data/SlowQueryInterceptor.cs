using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace HelpDesk.Api.Data;

// Interceptor de comandos: EF Core lo llama después de ejecutar cada sentencia.
// El log de EF (categoría Microsoft.EntityFrameworkCore.Database.Command) muestra TODAS, como Information, y en
// producción se apaga. Este deja solo las que pasan el umbral, como Warning y con el SQL completo, incluido el
// TagWith que dice de qué parte del código salió.
// - Mide lo mismo que el log de EF: hasta que la base responde, sin el tiempo de leer todas las filas.
// - Dapper usa la misma conexión, pero crea sus propios comandos: sus consultas NO pasan por aquí.
public sealed partial class SlowQueryInterceptor(
    IOptionsMonitor<SlowQueryOptions> options,
    ILogger<SlowQueryInterceptor> logger) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        WarnIfSlow(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        WarnIfSlow(command, eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        WarnIfSlow(command, eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        WarnIfSlow(command, eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        WarnIfSlow(command, eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        WarnIfSlow(command, eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void WarnIfSlow(DbCommand command, CommandExecutedEventData eventData)
    {
        int threshold = options.CurrentValue.ThresholdMilliseconds;

        if (eventData.Duration.TotalMilliseconds >= threshold)
        {
            LogSlowQuery(logger, (long)eventData.Duration.TotalMilliseconds, threshold, command.CommandText);
        }
    }

    // [LoggerMessage]: el método se genera al compilar, sin interpretar el mensaje ni crear objetos en cada llamada.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Consulta lenta: {ElapsedMilliseconds} ms (umbral {ThresholdMilliseconds} ms)\n{CommandText}")]
    private static partial void LogSlowQuery(ILogger logger, long elapsedMilliseconds, int thresholdMilliseconds, string commandText);
}
