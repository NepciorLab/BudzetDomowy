using System.Windows;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Remembers where the main window was and how big, and puts it back on the next start (never off-screen).</summary>
public static class WindowPlacement
{
    /// <summary>A window must overlap the visible desktop by at least this much on both axes to count as reachable.</summary>
    private const double MinVisible = 120;

    /// <summary>
    /// The saved rectangle, moved and shrunk if needed so it is reachable on the current screens
    /// (a monitor may have been unplugged since); null when it cannot be used at all.
    /// </summary>
    public static Rect? Fit(Rect saved, Rect virtualScreen, Size minimum)
    {
        if (saved.Width < 100 || saved.Height < 100 || double.IsNaN(saved.Left) || double.IsNaN(saved.Top)) return null;

        var width = Math.Min(Math.Max(saved.Width, minimum.Width), virtualScreen.Width);
        var height = Math.Min(Math.Max(saved.Height, minimum.Height), virtualScreen.Height);
        var rect = new Rect(saved.Left, saved.Top, width, height);

        var overlap = Rect.Intersect(rect, virtualScreen);
        if (overlap.IsEmpty || overlap.Width < MinVisible || overlap.Height < MinVisible)
            return null;

        // keep the title bar reachable: not above the top of the desktop
        var left = Math.Min(Math.Max(rect.Left, virtualScreen.Left - width + MinVisible), virtualScreen.Right - MinVisible);
        var top = Math.Min(Math.Max(rect.Top, virtualScreen.Top), virtualScreen.Bottom - MinVisible);
        return new Rect(left, top, width, height);
    }

    public static Rect VirtualScreen => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>Call before the window is shown.</summary>
    public static void Apply(Window window, AppSettings settings)
    {
        if (settings.WindowWidth is not { } w || settings.WindowHeight is not { } h
            || settings.WindowLeft is not { } l || settings.WindowTop is not { } t)
            return;

        var fitted = Fit(new Rect(l, t, w, h), VirtualScreen, new Size(window.MinWidth, window.MinHeight));
        if (fitted is not { } r) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = r.Left;
        window.Top = r.Top;
        window.Width = r.Width;
        window.Height = r.Height;
        if (settings.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Call when the window closes; a maximised window is saved with its normal (restore) size.</summary>
    public static AppSettings Capture(Window window, AppSettings settings)
    {
        var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100) return settings;
        return settings with
        {
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = window.WindowState == WindowState.Maximized,
        };
    }
}
