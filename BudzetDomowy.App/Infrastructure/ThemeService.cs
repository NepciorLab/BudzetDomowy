using ModernWpf;

namespace BudzetDomowy.App.Infrastructure;

public static class ThemeService
{
    /// <summary>System follows the Windows light/dark setting live.</summary>
    public static void Apply(ThemeChoice choice)
    {
        ThemeManager.Current.ApplicationTheme = choice switch
        {
            ThemeChoice.Light => ApplicationTheme.Light,
            ThemeChoice.Dark => ApplicationTheme.Dark,
            _ => null,
        };
    }
}
