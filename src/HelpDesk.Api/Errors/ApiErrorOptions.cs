namespace HelpDesk.Api.Errors;

public sealed class ApiErrorOptions
{
    public const string SectionName = "ApiErrors";

    // true solo en Development: los errores muestran el mensaje técnico (tipos .NET, posición en el JSON).
    // En Production se responde un mensaje genérico para no revelar detalles internos.
    public bool ExposeTechnicalDetails { get; set; }
}
