using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace HelpDesk.ServiceDefaults;

public static class DataProtectionSetup
{
    private const string KeysDirectory = "DataProtection:KeysDirectory";
    private const string RedisConnection = "DataProtection:RedisConnection";

    // Data Protection cifra las cookies de sesión y los tokens antifalsificación con unas llaves que genera la propia aplicación.
    // Por defecto quedan en el disco del usuario: en un contenedor, ese disco se pierde al recrearlo, y con él todas las sesiones.
    // Con DataProtection:KeysDirectory quedan en un volumen que sobrevive a la imagen. Sirve con una sola instancia.
    //
    // Con varias instancias, todas necesitan las mismas llaves: si no, lo que cifró una no lo abre otra, y una cookie deja de valer
    // al cambiar de pod. Un volumen compartido exige un disco que varios nodos puedan montar a la vez; Redis no, así que para eso
    // está DataProtection:RedisConnection. Cada instancia guarda las llaves en memoria, y sigue atendiendo si Redis se cae.
    //
    // En los dos casos las llaves quedan sin cifrar: quien las lea puede abrir las cookies. En la nube se envuelven además con un
    // almacén de llaves (ProtectKeysWith...).
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
        else if (configuration[RedisConnection] is { Length: > 0 } redis)
        {
            ConfigurationOptions options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false; // Redis puede arrancar después: la aplicación no debe caerse por eso
            var connection = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(options));

            dataProtection.PersistKeysToStackExchangeRedis(() => connection.Value.GetDatabase(), $"data-protection:{applicationName}");
        }

        return services;
    }
}
