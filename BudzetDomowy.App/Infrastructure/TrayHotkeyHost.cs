using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>The optional "stay handy" mode: a global shortcut (Ctrl+Alt+B) and a tray icon that open the quick entry window.</summary>
public interface IBackgroundHost : IDisposable
{
    bool IsActive { get; }

    /// <returns>false when the shortcut is already taken by another program; <paramref name="error"/> says so.</returns>
    bool Start(out string? error);

    void Stop();

    event Action? QuickAddRequested;
    event Action? OpenMainRequested;
    event Action? ExitRequested;
}

public sealed class TrayHotkeyHost : IBackgroundHost
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB001;
    private const uint VkB = 0x42;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private System.Windows.Forms.NotifyIcon? _icon;

    public bool IsActive => _source is not null;

    public event Action? QuickAddRequested;
    public event Action? OpenMainRequested;
    public event Action? ExitRequested;

    public bool Start(out string? error)
    {
        error = null;
        if (IsActive) return true;

        // a message-only window: it receives the hotkey without ever being visible
        var source = new HwndSource(new HwndSourceParameters("BudzetDomowyHotkey") { ParentWindow = HwndMessage, Width = 0, Height = 0 });
        if (!RegisterHotKey(source.Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkB))
        {
            error = Res.Format("BackgroundHotkeyTaken", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            source.Dispose();
            AppLog.Warn("Could not register Ctrl+Alt+B: " + error);
            return false;
        }

        source.AddHook(OnMessage);
        _source = source;
        CreateTrayIcon();
        AppLog.Info("Background mode started (Ctrl+Alt+B and tray icon).");
        return true;
    }

    public void Stop()
    {
        if (_source is { } source)
        {
            UnregisterHotKey(source.Handle, HotkeyId);
            source.RemoveHook(OnMessage);
            source.Dispose();
            _source = null;
        }
        if (_icon is { } icon)
        {
            icon.Visible = false;
            icon.Dispose();
            _icon = null;
        }
    }

    public void Dispose() => Stop();

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            QuickAddRequested?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void CreateTrayIcon()
    {
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = Res.Get("AppTitle"),
            Visible = true,
        };
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/BudzetDomowy.App;component/Assets/BudzetDomowy.ico"))?.Stream;
            if (stream is not null) icon.Icon = new System.Drawing.Icon(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            AppLog.Warn("Tray icon could not be loaded: " + e.Message);
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Res.Get("TrayQuickAdd"), null, (_, _) => QuickAddRequested?.Invoke());
        menu.Items.Add(Res.Get("TrayOpen"), null, (_, _) => OpenMainRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Res.Get("TrayExit"), null, (_, _) => ExitRequested?.Invoke());
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) QuickAddRequested?.Invoke();
        };
        icon.DoubleClick += (_, _) => OpenMainRequested?.Invoke();
        _icon = icon;
    }
}
