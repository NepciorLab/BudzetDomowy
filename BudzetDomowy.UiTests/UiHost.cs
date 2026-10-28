using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BudzetDomowy.UiTests;

/// <summary>
/// Runs WPF on one background STA thread with the real App resources (theme, styles) loaded. Nothing is ever shown:
/// views are laid out and drawn into bitmaps in memory, so tests never open a window or take the user's focus.
/// </summary>
public static class UiHost
{
    private static readonly Dispatcher UiDispatcher = Start();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var pl = new CultureInfo("pl-PL");
            CultureInfo.CurrentCulture = pl;
            CultureInfo.CurrentUICulture = pl;
            CultureInfo.DefaultThreadCurrentCulture = pl;
            CultureInfo.DefaultThreadCurrentUICulture = pl;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("pl-PL")));

            // a plain Application with the program's visual resources; the program's own startup code never runs
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/BudzetDomowy.App;component/Themes/AppResources.xaml", UriKind.Absolute),
            };

            BudzetDomowy.App.Infrastructure.DatePickerKeys.Register();
            dispatcher = Dispatcher.CurrentDispatcher;
            // an error raised by queued UI work (timers, bindings) must fail the test that caused it, not kill the whole test host
            dispatcher.UnhandledException += (_, e) =>
            {
                lock (UiErrors) UiErrors.Add(e.Exception);
                e.Handled = true;
            };
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WPF UI test thread" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    private static readonly List<Exception> UiErrors = [];

    /// <summary>Throws what the UI thread's queued work failed with since the last call.</summary>
    private static void ThrowQueuedErrors()
    {
        Pump();
        lock (UiErrors)
        {
            if (UiErrors.Count == 0) return;
            var errors = UiErrors.ToArray();
            UiErrors.Clear();
            throw new AggregateException("UI thread work failed", errors);
        }
    }

    public static void Run(Action action)
    {
        UiDispatcher.Invoke(action);
        UiDispatcher.Invoke(ThrowQueuedErrors);
    }

    public static T Run<T>(Func<T> func)
    {
        var result = UiDispatcher.Invoke(func);
        UiDispatcher.Invoke(ThrowQueuedErrors);
        return result;
    }

    /// <summary>Runs async code on the UI thread and waits for it from the calling (test) thread, which leaves the UI thread free to continue the awaits.</summary>
    public static void RunAsync(Func<Task> work)
    {
        UiDispatcher.InvokeAsync(work).Task.Unwrap().GetAwaiter().GetResult();
        UiDispatcher.Invoke(ThrowQueuedErrors);
    }

    public static string OutputDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("BUDZET_UI_OUT") ?? Path.Combine(Path.GetTempPath(), "budzet-ui");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Lays the element out at the given size (it never needs a window) and returns it ready for inspection.</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            Pump(); // rows, cells and templates are created by queued dispatcher work
        }
    }

    /// <summary>Runs everything that is queued on the dispatcher up to idle priority.</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>True when the element and all its ancestors are visible (collapsed panels hide their text).</summary>
    public static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    /// <summary>Draws the element on the theme's page background into a PNG file.</summary>
    public static string Render(FrameworkElement element, double width, double height, string name)
    {
        var page = new Border
        {
            Background = (Brush)Application.Current.FindResource("SystemControlPageBackgroundAltHighBrush"),
            Child = element,
        };
        // the real window sets the text colour for everything inside it; do the same here
        System.Windows.Documents.TextElement.SetForeground(page, (Brush)Application.Current.FindResource("SystemControlPageTextBaseHighBrush"));
        Layout(page, width, height);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var path = Path.Combine(OutputDir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        page.Child = null;
        return path;
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>The nearest ancestor of the type in the visual tree.</summary>
    public static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T t) return t;
        throw new InvalidOperationException("No ancestor of type " + typeof(T).Name);
    }

    /// <summary>All text the element shows (TextBlocks and TextBoxes), for assertions.</summary>
    public static List<string> Texts(DependencyObject root) =>
        Descendants<TextBlock>(root).Where(IsShown).Select(t => t.Text)
            .Concat(Descendants<TextBox>(root).Where(IsShown).Select(t => t.Text))
            .Where(s => !string.IsNullOrEmpty(s)).ToList();
}
