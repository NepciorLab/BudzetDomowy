using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// What a long operation is doing right now, shown on the bar beside the entry buttons ("Usuwanie wpisów: 1200…").
/// The work runs in the background, so the window stays alive, and the tables are refreshed once when it is over.
/// </summary>
public sealed partial class BusyStatus : ObservableObject
{
    public static BusyStatus Instance { get; } = new();

    /// <summary>From how many entries an operation counts as long (and is run in the background with a note on the bar).</summary>
    public static int BulkThreshold { get; set; } = 40;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isBusy;

    private DataHub? _hub;
    private int _running;

    /// <summary>How the work is run; tests replace it with a direct call.</summary>
    public Func<Action, Task> Executor { get; set; } = work => Task.Run(work);

    /// <summary>The hub that refreshes the screens; it waits with that until the operation is over.</summary>
    public void Attach(DataHub hub) => _hub = hub;

    public static bool IsBulk(int count) => count >= BulkThreshold;

    public async Task RunAsync(string text, Action work)
    {
        _running++;
        Text = text;
        IsBusy = true;
        var hold = _hub?.Hold();
        try
        {
            await Executor(work);
        }
        finally
        {
            hold?.Dispose();
            if (--_running == 0)
            {
                IsBusy = false;
                Text = "";
            }
        }
    }
}
