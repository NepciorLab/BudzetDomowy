using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// The small window shown while the program starts, so the user sees at once that something is happening and what. It lives on a thread
/// of its own (the main thread is busy opening the data while the program starts, a window there would stand still), so it takes its colours
/// from the setting directly instead of the resources of the application, which belong to the main thread.
/// </summary>
public sealed class StartupSplash
{
    private Dispatcher? _dispatcher;
    private TextBlock? _stage;
    private Window? _window;

    private StartupSplash()
    {
    }

    /// <summary>Opens the window and returns when it is on the screen (or after two seconds, when it cannot be).</summary>
    public static StartupSplash Show(ThemeChoice theme, string title, string version, string stage)
    {
        var splash = new StartupSplash();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                splash.Build(theme, title, version, stage);
                splash._window!.ContentRendered += (_, _) => ready.Set();
                splash._window.Show();
                splash._dispatcher = Dispatcher.CurrentDispatcher;
                Dispatcher.Run();
            }
            catch (Exception e)
            {
                AppLog.Warn("The start window could not be shown: " + e.Message);
            }
            finally
            {
                ready.Set();
            }
        }) { IsBackground = true, Name = "Start window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        return splash;
    }

    private static bool IsDark(ThemeChoice theme)
    {
        if (theme != ThemeChoice.System) return theme == ThemeChoice.Dark;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private void Build(ThemeChoice theme, string title, string version, string stage)
    {
        var dark = IsDark(theme);
        var background = dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF7, 0xF7, 0xF7);
        var text = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x20, 0x20, 0x20);
        var accent = Color.FromRgb(0xFF, 0xA7, 0x26);
        var muted = dark ? Color.FromRgb(0xB0, 0xB0, 0xB0) : Color.FromRgb(0x60, 0x60, 0x60);

        _stage = new TextBlock { Text = stage, Foreground = new SolidColorBrush(muted), FontSize = 14, Margin = new Thickness(0, 14, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        var panel = new StackPanel { Margin = new Thickness(32, 26, 32, 28), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(text), FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = version, Foreground = new SolidColorBrush(muted), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(_stage);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Foreground = new SolidColorBrush(accent), Background = new SolidColorBrush(dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(0) });

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,                                       // it never takes the keyboard from what the user is doing meanwhile
            Topmost = false,
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            Content = panel,
            Title = title,
        };
    }

    /// <summary>What the program is doing now.</summary>
    public void SetStage(string text)
    {
        if (_dispatcher is null) return;
        try { _dispatcher.BeginInvoke(() => { if (_stage is not null) _stage.Text = text; }); }
        catch (InvalidOperationException) { /* the window is gone already */ }
    }

    /// <summary>Closes the window and ends its thread.</summary>
    public void Close()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        _dispatcher = null;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                _window?.Close();
                dispatcher.InvokeShutdown();
            });
        }
        catch (InvalidOperationException) { /* ended already */ }
    }
}
