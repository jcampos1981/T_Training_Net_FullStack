using MudBlazor;

namespace HelpDesk.Admin.Components.Layout;

public static class AdminTheme
{
    public static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight { Primary = "#2563eb", AppbarBackground = "#1f2933" },
        // Fuentes del sistema, como el portal: MudBlazor sugiere Roboto desde Google Fonts, pero es una dependencia
        // externa más para un panel interno (y la Content-Security-Policy tendría que permitirla).
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["system-ui", "-apple-system", "Segoe UI", "sans-serif"] }
        }
    };
}
