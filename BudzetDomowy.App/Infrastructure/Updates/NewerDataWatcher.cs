using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure.Updates;

/// <summary>
/// Watches the synchronisation: when it finds data written by a newer version of the program, the update is offered at once
/// (once per run) instead of leaving the user with error messages.
/// </summary>
public sealed class NewerDataWatcher
{
    private readonly UpdateService _updates;
    private readonly Action<Func<Task>> _run;
    private bool _offered;

    /// <param name="run">Runs the offer on the UI thread.</param>
    public NewerDataWatcher(UpdateService updates, Action<Func<Task>> run)
    {
        _updates = updates;
        _run = run;
    }

    public void Observe(SyncStatus status)
    {
        if (!status.UpdateRequired || _offered) return;
        _offered = true;
        _run(() => _updates.OfferUpdateAsync(dataIsNewer: true));
    }
}
