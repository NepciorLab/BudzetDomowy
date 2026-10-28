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
    private readonly object _gate = new();
    private int _holds;
    private bool _pending;
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

    /// <summary>
    /// Keeps the screens from refreshing until the returned object is disposed (a long operation writes thousands of changes);
    /// one refresh follows if anything changed meanwhile.
    /// </summary>
    public IDisposable Hold()
    {
        lock (_gate) _holds++;
        return new Release(this);
    }

    private sealed class Release(DataHub hub) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            bool refresh;
            lock (hub._gate)
            {
                hub._holds--;
                refresh = hub._holds == 0 && hub._pending;
                if (refresh) hub._pending = false;
            }
            if (refresh) hub.Request();
        }
    }

    /// <summary>Tells the screens at once (on the UI thread, without the short wait) that the user has just changed something on another screen.</summary>
    public void NotifyNow()
    {
        if (_stopped) return;
        _timer.Stop();                       // the notification that was waiting is this one
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Request()
    {
        if (_stopped) return;
        lock (_gate)
        {
            if (_holds > 0)
            {
                _pending = true;
                return;
            }
        }
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
