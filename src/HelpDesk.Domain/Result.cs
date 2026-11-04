namespace HelpDesk.Domain;

public enum ErrorKind
{
    NotFound,
    Forbidden,
    Conflict
}

// Un fallo ESPERADO: es una respuesta posible del caso de uso, no un accidente. Un ticket cerrado que no se puede editar es
// parte del negocio. Lo inesperado (la base caída, un bug) sigue siendo una excepción.
public sealed record Error(ErrorKind Kind, string Title, string Detail)
{
    public static readonly Error NotFound = new(ErrorKind.NotFound, "No encontrado", "El recurso no existe.");

    public static readonly Error Forbidden = new(ErrorKind.Forbidden, "Sin permiso", "No puedes realizar esta operación.");

    public static readonly Error EditConflict = new(
        ErrorKind.Conflict,
        "Conflicto de edición",
        "Alguien más modificó este registro desde que lo consultaste. Vuelve a cargarlo y aplica tus cambios de nuevo.");

    // Los mensajes de reglas de negocio están escritos para el usuario.
    public static Error BusinessRule(string detail) => new(ErrorKind.Conflict, "Regla de negocio incumplida", detail);
}

// Lo que necesita el pipeline del mediador para saber cómo terminó un caso de uso, sin importar qué devuelve.
public interface IFallible
{
    Error? Failure { get; }
}

// El resultado de una operación que puede fallar de forma esperada: la firma lo dice, y el compilador obliga a mirarlo.
// Con una excepción, el que llama no se entera de que existe ese camino.
public sealed class Result<T> : IFallible
{
    private readonly T? value;

    private Result(T? value, Error? failure)
    {
        this.value = value;
        Failure = failure;
    }

    public Error? Failure { get; }

    public bool IsSuccess => Failure is null;

    public T Value => IsSuccess ? value! : throw new InvalidOperationException($"El resultado falló: {Failure!.Detail}");

    public Error Error => Failure ?? throw new InvalidOperationException("El resultado fue exitoso: no tiene error.");

    public static Result<T> Success(T value) => new(value, failure: null);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => new(default, error);
}

// Para las operaciones exitosas que no tienen nada que devolver.
public readonly record struct Unit;
