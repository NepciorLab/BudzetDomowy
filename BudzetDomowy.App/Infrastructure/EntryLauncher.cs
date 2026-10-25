using System.Windows;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Opens the window for a new expense or income.</summary>
public interface IEntryLauncher
{
    void Open(bool income);
}

public sealed class WpfEntryLauncher : IEntryLauncher
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private EntryWindow? _open;

    public WpfEntryLauncher(BudgetCore core, ToastService toast)
    {
        _core = core;
        _toast = toast;
    }

    public void Open(bool income)
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();                    // one entry window at a time
            return;
        }

        var window = new EntryWindow(new EntryViewModel(_core, _toast, income));
        if (Application.Current?.MainWindow is { IsVisible: true } main && main != window) window.Owner = main;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = window;
        window.Closed += (_, _) => _open = null;
        window.ShowDialog();
    }
}
