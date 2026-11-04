using Microsoft.Extensions.Configuration;

namespace HelpDesk.AppHost;

public static class EnvFile
{
    private const string ParametersSection = "Parameters";

    // El nombre de un parámetro de Aspire solo admite letras, números y guiones: MSSQL_SA_PASSWORD se llama mssql-sa-password.
    public static string ParameterNameOf(string variable) => variable.ToLowerInvariant().Replace('_', '-');

    // Aspire lee sus parámetros de la sección Parameters. Se cargan de deploy/.env, el archivo que ya existe, y sin imprimir nada.
    public static IConfigurationBuilder AddEnvFile(this IConfigurationBuilder configuration, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../", relativePath));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Falta {path}. Plantilla: deploy/.env.example", path);
        }

        return configuration.AddInMemoryCollection(Parse(File.ReadLines(path)));
    }

    private static IEnumerable<KeyValuePair<string, string?>> Parse(IEnumerable<string> lines) =>
        from line in lines
        where !line.StartsWith('#') && line.Contains('=')
        let parts = line.Split('=', 2)
        select KeyValuePair.Create<string, string?>($"{ParametersSection}:{ParameterNameOf(parts[0].Trim())}", parts[1].Trim().Trim('\''));
}
