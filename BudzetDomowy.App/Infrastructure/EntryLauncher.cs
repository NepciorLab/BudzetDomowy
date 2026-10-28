using System.Windows;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.App.Views;
using BudzetDomowy.Core;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Opens the window for a new expense or income, and the calculator.</summary>
public interface IEntryLauncher
{
    void Open(bool income);

    void OpenCalculator();
}

public sealed class WpfEntryLauncher : IEntryLauncher
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private Window? _open;
    private Window? _calculator;

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

        // after an expense is saved the next one is started at once (an empty window, as if the button was pressed again); Cancel or Esc ends it
        bool saved;
        do
        {
            var vm = new EntryViewModel(_core, _toast, income, new WpfDuplicatePrompt());
            Window window = income ? new IncomeWindow(vm) : new EntryWindow(vm);          // an income is simpler: no place, no items, no event
            if (Application.Current?.MainWindow is { IsVisible: true } main && main != window) window.Owner = main;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open = window;
            window.Closed += (_, _) => _open = null;
            saved = window.ShowDialog() == true;
        }
        while (saved && !income);
    }

    /// <summary>The calculator is not modal: it stays open beside the main window; asking again brings it to the front.</summary>
    public void OpenCalculator()
    {
        if (_calculator is { IsLoaded: true })
        {
            if (_calculator.WindowState == WindowState.Minimized) _calculator.WindowState = WindowState.Normal;
            _calculator.Activate();
            return;
        }

        var window = new CalculatorWindow(new CalculatorViewModel());
        if (Application.Current?.MainWindow is { IsVisible: true } main) window.Owner = main;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _calculator = window;
        window.Closed += (_, _) => _calculator = null;
        window.Show();
    }
}
