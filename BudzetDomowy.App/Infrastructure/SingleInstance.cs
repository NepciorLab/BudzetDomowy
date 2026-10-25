using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// A second start of the program does not open a message: it asks the copy that is already running to show its window, and ends.
/// The first copy waits for that request on a named event; the second one sets the event.
/// </summary>
public static class SingleInstance
{
    private const int AnyProcess = -1;

    public static string EventName(string? profile) => @"Local\BudzetDomowy-" + (profile ?? "default") + "-show";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>The first copy: <paramref name="onRequest"/> runs (on a pool thread) every time a later copy asks for the window.</summary>
    public static IDisposable Listen(string eventName, Action onRequest)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => onRequest(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(handle, registration);
    }

    /// <summary>
    /// A later copy: asks the running one to come to the front. The right to take the foreground, which this just started copy has, is
    /// passed on to it. False when nobody listens (a copy of an older version, which only holds the lock, or one that is still starting
    /// and has not yet got to listen within <paramref name="wait"/>).
    /// </summary>
    public static bool Request(string eventName, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
            {
                using (handle)
                {
                    AllowSetForegroundWindow(AnyProcess);
                    return handle.Set();
                }
            }
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(100);
        }
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }

    /// <summary>
    /// Brings the program to the front: the main window is shown again if it was hidden, maximised, and put above the other windows;
    /// when a dialog of the program is open (the main window is then disabled) the dialog is the one that comes forward.
    /// </summary>
    public static void BringToFront(Window? main, IEnumerable<Window> all)
    {
        var windows = all.ToList();
        var target = main is { IsLoaded: true } ? main : windows.FirstOrDefault(w => w.IsVisible);
        if (target is null) return;

        if (ReferenceEquals(target, main))
        {
            if (!target.IsVisible) target.Show();
            if (target.WindowState != WindowState.Maximized) target.WindowState = WindowState.Maximized;
        }
        else if (target.WindowState == WindowState.Minimized)
        {
            target.WindowState = WindowState.Normal;
        }

        // a modal dialog (or a window owned by one) is what the user has to answer first
        var front = target;
        while (front.OwnedWindows.OfType<Window>().LastOrDefault(w => w.IsVisible) is { } child) front = child;

        // Windows lets the foreground change only for a short time after the user's own start; a brief "topmost" makes the window come above the others too
        var wasTopmost = front.Topmost;
        front.Topmost = true;
        front.Activate();
        front.Topmost = wasTopmost;
        if (new WindowInteropHelper(front).Handle is { } handle && handle != IntPtr.Zero) SetForegroundWindow(handle);
        front.Focus();
    }
}
