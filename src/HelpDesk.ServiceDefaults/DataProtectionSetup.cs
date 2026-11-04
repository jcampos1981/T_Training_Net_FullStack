using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.ServiceDefaults;

public static class DataProtectionSetup
{
    private const string KeysDirectory = "DataProtection:KeysDirectory";

    // Data Protection cifra las cookies de sesión y los tokens antifalsificación con unas llaves que genera la propia aplicación.
    // Por defecto quedan en el disco del usuario: en un contenedor, ese disco se pierde al recrearlo, y con él todas las sesiones.
    // Con DataProtection:KeysDirectory quedan en un volumen que sobrevive a la imagen.
    //
    // En ese volumen las llaves quedan sin cifrar: quien lo lea puede abrir las cookies. En la nube se envuelven además con un
    // almacén de llaves (ProtectKeysWith...), y varias instancias de la misma aplicación tienen que compartir el directorio.
    //
    // El nombre de la aplicación es fijo porque, sin él, las llaves se aíslan por la ruta de instalación: mover la aplicación a
    // otra carpeta invalidaría todo lo que cifró.
    public static IServiceCollection AddHelpDeskDataProtection(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        IDataProtectionBuilder dataProtection = services.AddDataProtection().SetApplicationName(applicationName);

        if (configuration[KeysDirectory] is { Length: > 0 } directory)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(directory));
        }

        return services;
    }
}
