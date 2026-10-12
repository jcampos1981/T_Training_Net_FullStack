namespace HelpDesk.Importer.Import;

public sealed class ImportOptions
{
    // Si es relativa, se resuelve desde la carpeta del ejecutable.
    public string Folder { get; set; } = "samples";
}
