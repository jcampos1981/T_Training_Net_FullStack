using System.Globalization;

namespace HelpDesk.Importer.Common;

public sealed record ImporterArguments(string Folder, DateTimeOffset? AsOf)
{
    public const string Usage = "Uso: HelpDesk.Importer [carpeta] [--as-of <fecha ISO 8601>]";

    private const string AsOfOption = "--as-of";

    public static ImporterArguments? Parse(string[] args, string defaultFolder) => args switch
    {
        [] => new(defaultFolder, AsOf: null),
        [var folder] when !folder.StartsWith("--", StringComparison.Ordinal) => new(folder, AsOf: null),
        [AsOfOption, var date] => WithAsOf(defaultFolder, date),
        [var folder, AsOfOption, var date] => WithAsOf(folder, date),
        _ => null
    };

    public TimeProvider CreateClock() =>
        AsOf is { } instant ? new FixedTimeProvider(instant) : TimeProvider.System;

    private static ImporterArguments? WithAsOf(string folder, string date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset asOf)
            ? new(folder, asOf)
            : null;
}
