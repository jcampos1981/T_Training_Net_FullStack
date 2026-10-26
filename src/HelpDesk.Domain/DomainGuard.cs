namespace HelpDesk.Domain;

// Validaciones repetidas del dominio en un solo lugar.
internal static class DomainGuard
{
    public static string NotBlank(string value, string paramName, string message) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(message, paramName)
            : value.Trim();
}
