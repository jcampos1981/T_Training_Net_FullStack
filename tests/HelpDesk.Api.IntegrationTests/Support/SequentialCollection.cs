namespace HelpDesk.Api.IntegrationTests.Support;

// xUnit corre las clases en paralelo. Estas no pueden: un barrido de SLA crea alertas de TODOS los tickets vencidos, así que
// dos a la vez se quitarían las alertas entre sí, y cada instancia nueva de la API hace un barrido al arrancar.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialCollection
{
    public const string Name = "Barridos de SLA";
}
