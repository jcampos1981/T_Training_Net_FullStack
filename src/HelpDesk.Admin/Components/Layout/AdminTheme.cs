using MudBlazor;

namespace HelpDesk.Admin.Components.Layout;

public static class AdminTheme
{
    public static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2563eb",
            AppbarBackground = "#1f2933",
            // Los colores de estado también se usan como texto (avisos y etiquetas). Los de MudBlazor son muy claros para
            // leerlos sobre fondo blanco: estos tonos más oscuros superan el contraste 4,5:1 que pide WCAG AA.
            Info = "#0369a1",
            Success = "#15803d",
            Warning = "#b45309",
            Error = "#b91c1c"
        },
        // Fuentes del sistema, como el portal: MudBlazor sugiere Roboto desde Google Fonts, pero es una dependencia
        // externa más para un panel interno (y la Content-Security-Policy tendría que permitirla).
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["system-ui", "-apple-system", "Segoe UI", "sans-serif"] }
        }
    };
}
