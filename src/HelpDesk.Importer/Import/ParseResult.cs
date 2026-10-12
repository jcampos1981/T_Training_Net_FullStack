using HelpDesk.Domain;

namespace HelpDesk.Importer.Import;

public abstract record ParseResult
{
    // Constructor privado: solo los tipos anidados pueden heredar, así que no hay más casos posibles.
    private ParseResult() { }

    public sealed record Success(Ticket Ticket) : ParseResult;

    public sealed record Failure(int LineNumber, string Reason) : ParseResult;
}
