using System.Windows.Threading;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Raises one coalesced "data changed" notification on the UI thread whenever local edits happen or
/// synchronisation brought changes from another device.
/// </summary>
public sealed class DataHub : IDisposable
{
    private bool _stopped;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    public event EventHandler? Changed;

    public DataHub(BudgetCore core)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_stopped) Changed?.Invoke(this, EventArgs.Empty);
        };
        core.Recorder.Changed += (_, _) => Request();
        core.Sync.StatusChanged += (_, s) =>
        {
            if (s.State != SyncState.Syncing && s.LastApplied.Applied > 0) Request();
        };
    }

    public void Request()
    {
        if (_stopped) return;
        if (_dispatcher.CheckAccess()) _timer.Start();
        else _dispatcher.BeginInvoke(() => _timer.Start());
    }

    /// <summary>Stops notifying (pending notifications are dropped).</summary>
    public void Dispose()
    {
        _stopped = true;
        if (_dispatcher.CheckAccess()) _timer.Stop();
        else _dispatcher.Invoke(() => _timer.Stop());
    }
}
