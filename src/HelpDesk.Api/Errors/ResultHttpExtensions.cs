using HelpDesk.Domain;

namespace HelpDesk.Api.Errors;

// El único lugar que traduce un fallo esperado a HTTP. El caso de uso no sabe qué es un 409: dice "conflicto", y aquí se decide.
public static class ResultHttpExtensions
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToHttp();

    public static IResult ToHttp(this Error error) => error.Kind switch
    {
        ErrorKind.NotFound => TypedResults.NotFound(),
        ErrorKind.Forbidden => TypedResults.Forbid(),
        ErrorKind.Conflict => TypedResults.Problem(error.Detail, statusCode: StatusCodes.Status409Conflict, title: error.Title),
        ErrorKind.Unavailable => TypedResults.Problem(error.Detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: error.Title),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error.Kind, "Tipo de error sin respuesta HTTP.")
    };
}
