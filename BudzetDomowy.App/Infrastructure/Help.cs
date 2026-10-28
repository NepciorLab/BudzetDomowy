using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BudzetDomowy.App.Views;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Marks the part of a screen that a help topic describes: <c>infra:Help.Topic="transactions.filters"</c>.
/// An element without its own topic belongs to the nearest parent that has one.
/// </summary>
public static class Help
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.RegisterAttached("Topic", typeof(string), typeof(Help), new PropertyMetadata(null));

    public static string? GetTopic(DependencyObject element) => (string?)element.GetValue(TopicProperty);

    public static void SetTopic(DependencyObject element, string? value) => element.SetValue(TopicProperty, value);

    /// <summary>The topic of the element or of the nearest parent that has one (null when none has).</summary>
    public static string? TopicOf(DependencyObject? start)
    {
        for (var d = start; d is not null; d = ParentOf(d))
            if (GetTopic(d) is { Length: > 0 } topic) return topic;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject d)
    {
        if (d is System.Windows.Controls.ContextMenu { PlacementTarget: { } owner }) return owner;       // a menu belongs to what it was opened on
        if (d is Visual or Visual3D && VisualTreeHelper.GetParent(d) is { } visual) return visual;

        // the top of a pop-up (a drop-down list, a menu) hangs on its owner only logically
        var logical = d switch
        {
            FrameworkElement fe => fe.Parent ?? fe.TemplatedParent,
            FrameworkContentElement fce => fce.Parent ?? fce.TemplatedParent,
            _ => null,
        };
        return logical is Popup popup ? popup.PlacementTarget ?? popup.Parent : logical;
    }
}

/// <summary>
/// The help of the program: F1 (or the key chosen in Settings) opens it for the field that has the focus,
/// the button with the question mark turns the mouse into a pointer with a question mark, and the next click opens
/// the help of whatever was clicked.
/// </summary>
public static class HelpService
{
    private static bool _installed;
    private static HelpWindow? _window;

    /// <summary>True while the mouse waits for a click on the thing to be described.</summary>
    public static bool IsPicking { get; private set; }

    public static event Action? PickingChanged;

    /// <summary>The topic of the screen that is open in the main window (what F1 shows when the focus is not on a field).</summary>
    public static Func<string?>? PageTopic { get; set; }

    /// <summary>Tests set this to learn which topic was asked for without a window opening.</summary>
    public static Action<string?>? ShowHook { get; set; }

    /// <summary>Hooks the keyboard and the mouse of the whole program; safe to call again.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.KeyDownEvent, new KeyEventHandler(OnKeyDown));
        EventManager.RegisterClassHandler(typeof(UIElement), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
    }

    // ----- the question mark -----

    public static void StartPicking()
    {
        if (IsPicking) return;
        IsPicking = true;
        Mouse.OverrideCursor = Cursors.Help;
        PickingChanged?.Invoke();
    }

    public static void StopPicking()
    {
        if (!IsPicking) return;
        IsPicking = false;
        Mouse.OverrideCursor = null;
        PickingChanged?.Invoke();
    }

    public static void TogglePicking()
    {
        if (IsPicking) StopPicking(); else StartPicking();
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPicking || e.Handled || e.Key != Key.Escape) return;
        StopPicking();                       // back to normal: Esc does nothing else this time
        e.Handled = true;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPicking || e.Handled) return;
        e.Handled = true;                    // the click only asks what the thing is; it does not operate it
        var topic = Help.TopicOf(e.OriginalSource as DependencyObject);
        StopPicking();
        if (topic == "help.button") return;  // the same button again: back to normal
        Show(topic ?? PageTopic?.Invoke());
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !ShortcutMap.Instance.Matches(ShortcutMap.Help, e)) return;
        if (sender is DependencyObject source && Window.GetWindow(source) is HelpWindow) return;   // already in the help
        e.Handled = true;
        var topic = Help.TopicOf(Keyboard.FocusedElement as DependencyObject);
        if (topic is null or "main.nav" or "help.button") topic = PageTopic?.Invoke();   // the focus is not on a field: describe the open screen
        Show(topic);
    }

    // ----- the window -----

    /// <summary>Shows the help window (one for the whole program) at the topic, or at the first one.</summary>
    public static void Show(string? topic)
    {
        if (ShowHook is not null)
        {
            ShowHook(topic);
            return;
        }

        if (_window is { IsLoaded: true } open && IsEnabled(open))
        {
            open.ShowTopic(topic);
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        _window?.Close();                    // it was left behind a modal window (disabled by it): make a fresh one
        var window = new HelpWindow();
        _window = window;
        window.Closed += (_, _) =>
        {
            if (_window == window) _window = null;
        };
        window.ShowTopic(topic);
        window.Show();
    }

    private static bool IsEnabled(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero || IsWindowEnabled(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);
}
