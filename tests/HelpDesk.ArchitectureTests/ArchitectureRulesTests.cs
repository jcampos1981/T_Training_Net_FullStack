using System.Reflection;
using System.Xml.Linq;
using HelpDesk.Api.Features.Portal;
using HelpDesk.Api.Mediation;
using HelpDesk.Domain;
using NetArchTest.Rules;
using TestResult = NetArchTest.Rules.TestResult;

namespace HelpDesk.ArchitectureTests;

public sealed class ArchitectureRulesTests
{
    private static readonly Assembly Domain = typeof(Ticket).Assembly;
    private static readonly Assembly Api = typeof(OpenTicket).Assembly;

    // La protección que Clean Architecture da con proyectos separados: el dominio no sabe de nadie.
    [Fact]
    public void Domain_DependsOnNothingElse()
    {
        TestResult result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions", "Dapper", "HelpDesk.Api")
            .GetResult();

        AssertPasses(result);
    }

    // Un caso de uso no sabe de HTTP: eso lo traduce su endpoint. Por eso se puede probar sin levantar la API.
    [Fact]
    public void Handlers_DoNotKnowHttp()
    {
        TestResult result = Handlers()
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing")
            .GetResult();

        AssertPasses(result);
    }

    // Los casos de uso usan repositorios y consultas. Solo estos dos tienen una razón para tocar EF: guardan dos tablas en una
    // sola transacción. Si aparece otro, o uno deja de hacerlo, esta prueba obliga a revisar la lista.
    [Fact]
    public void Handlers_OnlyTheKnownOnesUseEfDirectly()
    {
        TestResult result = Handlers().ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        string[] expected =
        [
            "HelpDesk.Api.Features.Agents.ReassignTickets/Handler",
            "HelpDesk.Api.Features.Portal.OpenTicket/Handler"
        ];

        Assert.Equal(expected, (result.FailingTypeNames ?? []).Order());
    }

    [Fact]
    public void Handlers_AreSealed()
    {
        AssertPasses(Handlers().Should().BeSealed().GetResult());
    }

    // Registrar los handlers es automático: uno que falte recién fallaría cuando alguien llame a ese caso de uso.
    [Fact]
    public void EveryRequest_HasExactlyOneHandler()
    {
        Type[] types = Api.GetTypes();
        Type[] requests = [.. types.Where(type => type is { IsClass: true, IsAbstract: false } && Contracts(type, typeof(IRequest<>)).Any())];

        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
        {
            int handlers = types.Count(type => Contracts(type, typeof(IRequestHandler<,>)).Any(contract => contract.GetGenericArguments()[0] == request));
            Assert.True(handlers == 1, $"{request.FullName} tiene {handlers} handlers.");
        });
    }

    // Otra audiencia, otro contrato: si el portal usara TicketResponse, un dato interno nuevo le llegaría al cliente.
    [Fact]
    public void Portal_DoesNotUseTheTeamContract()
    {
        TestResult result = Types.InAssembly(Api)
            .That()
            .ResideInNamespace("HelpDesk.Api.Features.Portal")
            .ShouldNot()
            .HaveDependencyOn("HelpDesk.Api.Features.Tickets.TicketResponse")
            .GetResult();

        AssertPasses(result);
    }

    // El portal es para una audiencia: nada del equipo debería depender de él. Solo Program, que arma la aplicación, y los datos
    // de ejemplo, que abren tickets como lo haría un cliente.
    [Fact]
    public void NothingDependsOnThePortal_ExceptProgramAndTheSampleData()
    {
        TestResult result = Types.InAssembly(Api)
            .That()
            .DoNotResideInNamespace("HelpDesk.Api.Features.Portal")
            .ShouldNot()
            .HaveDependencyOn("HelpDesk.Api.Features.Portal")
            .GetResult();

        Assert.Equal(["HelpDesk.Api.Data.DevelopmentSeeder", "Program"], KeepOuterTypes(result.FailingTypeNames ?? []));
    }

    // El panel y el BFF hablan con la API por HTTP, como cualquier cliente, y el worker solo conoce sus eventos. Una referencia al proyecto los
    // acoplaría en silencio.
    [Theory]
    [InlineData("HelpDesk.Admin")]
    [InlineData("HelpDesk.Bff")]
    [InlineData("HelpDesk.Worker")]
    public void Frontends_DoNotReferenceTheApi(string project)
    {
        XDocument file = XDocument.Load(Path.Combine(RepositoryRoot(), "src", project, $"{project}.csproj"));

        string[] references = [.. file.Descendants("ProjectReference").Select(reference => reference.Attribute("Include")!.Value)];

        Assert.DoesNotContain(references, reference => reference.Contains("HelpDesk.Api") || reference.Contains("HelpDesk.Domain"));
    }

    private static PredicateList Handlers() => Types.InAssembly(Api).That().HaveName("Handler");

    private static IEnumerable<Type> Contracts(Type type, Type genericContract) =>
        type.GetInterfaces().Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == genericContract);

    // Una clase genera tipos internos (lambdas, máquinas de estado): interesa la clase que escribió alguien.
    private static string[] KeepOuterTypes(IEnumerable<string> names) =>
        [.. names.Select(name => name.Split('+', '/')[0]).Distinct().Order()];

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HelpDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    private static void AssertPasses(TestResult result) =>
        Assert.True(result.IsSuccessful, $"No cumplen la regla: {string.Join(", ", result.FailingTypeNames ?? [])}");
}
