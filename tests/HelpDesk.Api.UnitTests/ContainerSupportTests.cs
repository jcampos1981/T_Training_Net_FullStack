using System.Security.Cryptography;
using HelpDesk.ServiceDefaults;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HelpDesk.Api.UnitTests;

public sealed class HealthProbeTests
{
    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("9000", 9000)]
    [InlineData("9000;9001", 9000)]
    public void AsksTheFirstPortOfTheInstance(string? ports, int expected)
    {
        Assert.Equal(new Uri($"http://127.0.0.1:{expected}/health/ready"), HealthProbe.ReadinessAddress(ports));
    }

    [Fact]
    public void OnlyRunsWhenAsked()
    {
        Assert.True(HealthProbe.IsRequested(["--health-probe"]));
        Assert.False(HealthProbe.IsRequested([]));
        Assert.False(HealthProbe.IsRequested(["--urls", "http://+:8080"]));
    }
}

public sealed class KeycloakDiscoveryTests
{
    private const string WellKnown = ".well-known/openid-configuration";

    [Fact]
    public void WithoutKeycloak_ThereIsNothingToAsk()
    {
        Assert.Null(KeycloakDiscovery.DocumentAddress(Configuration()));
    }

    [Theory]
    [InlineData("http://localhost:8080/realms/helpdesk")]
    [InlineData("http://localhost:8080/realms/helpdesk/")]
    public void ByDefault_ItIsUnderTheAuthority(string authority)
    {
        Uri? address = KeycloakDiscovery.DocumentAddress(Configuration(("Keycloak:Authority", authority)));

        Assert.Equal(new Uri($"http://localhost:8080/realms/helpdesk/{WellKnown}"), address);
    }

    // En contenedores, Authority es lo que ve el navegador: desde el servidor, "localhost" es el propio contenedor.
    [Fact]
    public void TheInternalAddress_WinsOverTheAuthority()
    {
        Uri? address = KeycloakDiscovery.DocumentAddress(Configuration(
            ("Keycloak:Authority", "http://localhost:8080/realms/helpdesk"),
            ("Keycloak:MetadataAddress", $"http://keycloak:8080/realms/helpdesk/{WellKnown}")));

        Assert.Equal(new Uri($"http://keycloak:8080/realms/helpdesk/{WellKnown}"), address);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create<string, string?>(setting.Key, setting.Value)))
            .Build();
}

public sealed class DataProtectionSetupTests : IDisposable
{
    private readonly DirectoryInfo keys = Directory.CreateTempSubdirectory("helpdesk-keys-");

    // Es lo que hace que una sesión sobreviva a recrear el contenedor, o que la abra otra instancia.
    [Fact]
    public void AnotherInstanceWithTheSameKeys_ReadsWhatTheFirstProtected()
    {
        string secret = Protector("HelpDesk.Bff").Protect("sesión");

        Assert.Equal("sesión", Protector("HelpDesk.Bff").Unprotect(secret));
        Assert.NotEmpty(keys.GetFiles("key-*.xml"));
    }

    [Fact]
    public void AnotherApplication_CannotReadIt_EvenWithTheSameKeys()
    {
        string secret = Protector("HelpDesk.Bff").Protect("sesión");

        Assert.ThrowsAny<CryptographicException>(() => Protector("HelpDesk.Admin").Unprotect(secret));
    }

    public void Dispose() => keys.Delete(recursive: true);

    private IDataProtector Protector(string application)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("DataProtection:KeysDirectory", keys.FullName)])
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddHelpDeskDataProtection(configuration, application)
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("prueba");
    }
}
