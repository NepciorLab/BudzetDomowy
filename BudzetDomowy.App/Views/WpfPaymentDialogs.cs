using System.Windows;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;

namespace BudzetDomowy.App.Views;

/// <summary>Opens the windows of the payments above the main window.</summary>
public sealed class WpfPaymentDialogs : IPaymentDialogs
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;
    private readonly IMessageDialogs _messages;

    public WpfPaymentDialogs(BudgetCore core, ToastService toast, IMessageDialogs messages)
    {
        _core = core;
        _toast = toast;
        _messages = messages;
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } main ? main : null;

    private static void Show(Window window)
    {
        // a window above another dialog (the reminder, the entry) belongs to the one that is active
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != window) ?? Owner;
        if (owner is not null && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void OpenEntry(PlannedPayment? existing = null) => Show(new PaymentEntryWindow(new PaymentEntryViewModel(_core, this, _toast, existing)));

    public void ManagePayees() => Show(new PayeesWindow(new PayeesViewModel(_core, this, _messages)));

    public Payee? EditPayee(Payee? existing)
    {
        var vm = new PayeeEditViewModel(_core, existing);
        Show(new PayeeWindow(vm));
        return vm.Result;
    }

    public PayResult ShowPay(PlannedPayment payment)
    {
        var vm = new PaymentPayViewModel(_core, _toast, payment);
        Show(new PayWindow(vm));
        return vm.Result;
    }
}

/// <summary>The reminder about a bill, in a small window; only while the main window is on screen.</summary>
public sealed class WpfPaymentReminderPrompt : IPaymentReminderPrompt
{
    public bool CanAsk => Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

    public ReminderAnswer Ask(PaymentReminder reminder)
    {
        var window = new PaymentReminderWindow(reminder) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return window.Answer;
    }
}
