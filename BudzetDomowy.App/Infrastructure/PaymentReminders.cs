using System.Windows.Threading;
using BudzetDomowy.App.ViewModels;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.App.Infrastructure;

public enum ReminderAnswer { Later, PayNow, Paid }

/// <summary>What the reminder window shows about one bill.</summary>
public sealed record PaymentReminder(PlannedPayment Payment, string PayeeName, string Account, string Title, string Amount, string Due, string Relative, bool IsLate);

public interface IPaymentReminderPrompt
{
    /// <summary>False while there is no visible program window to ask in (the reminder waits).</summary>
    bool CanAsk { get; }

    ReminderAnswer Ask(PaymentReminder reminder);
}

public sealed record ReminderOutcome(int Settled, int Paid, int Postponed, int Waiting);

/// <summary>
/// Looks after the planned payments: bills that were scheduled and are past their date are settled, and bills that are due within three
/// days (or late) are asked about at every start of the program until they are paid or scheduled.
/// Answers: later (asked again at the next start), pay now (the QR code opens), paid (booked with today's date and taken off the list).
/// </summary>
public sealed class PaymentReminderRunner
{
    private readonly BudgetCore _core;
    private readonly IPaymentReminderPrompt _prompt;
    private readonly IPaymentDialogs _dialogs;
    private readonly HashSet<Guid> _postponed = [];
    private DispatcherTimer? _timer;
    private bool _busy;

    public PaymentReminderRunner(BudgetCore core, IPaymentReminderPrompt prompt, IPaymentDialogs dialogs)
    {
        _core = core;
        _prompt = prompt;
        _dialogs = dialogs;
    }

    /// <summary>The first check soon after the start, then every hour (a program left open over midnight reminds the next day too).</summary>
    public void Start(TimeSpan firstDelay, TimeSpan every)
    {
        _timer?.Stop();
        var first = true;
        _timer = new DispatcherTimer { Interval = firstDelay };
        _timer.Tick += (_, _) =>
        {
            if (first)
            {
                first = false;
                _timer!.Interval = every;
            }
            try { Run(DateOnly.FromDateTime(DateTime.Today)); }
            catch (Exception e) when (e is UpdateRequiredException or IOException)
            {
                AppLog.Warn("Checking the payments failed: " + e.Message);
            }
        };
        _timer.Start();
    }

    public ReminderOutcome Run(DateOnly today, bool askAgain = false)
    {
        if (_busy) return new ReminderOutcome(0, 0, 0, 0);
        _busy = true;
        try
        {
            var settled = _core.Payments.SettleScheduled(today);
            if (askAgain) _postponed.Clear();

            var paid = 0;
            var postponed = 0;
            var waiting = 0;
            var payees = _core.Reader.GetPayees().ToDictionary(p => p.Id);
            foreach (var payment in _core.Payments.DueForReminder(today))
            {
                if (_postponed.Contains(payment.Id))
                {
                    postponed++;
                    continue;
                }
                if (!_prompt.CanAsk)
                {
                    waiting++;
                    continue;
                }

                // an earlier answer may have dealt with it already (paid or scheduled from the QR window)
                if (_core.Reader.GetPlannedPayments().FirstOrDefault(p => p.Id == payment.Id) is not { Status: PaymentStatus.Pending } current) continue;
                switch (_prompt.Ask(Describe(current, payees.GetValueOrDefault(current.PayeeId), today)))
                {
                    case ReminderAnswer.Paid:
                        _core.Payments.MarkPaid(current.Id, today);
                        paid++;
                        break;
                    case ReminderAnswer.PayNow:
                        var result = _dialogs.ShowPay(current);
                        if (result == PayResult.Paid) paid++;
                        else if (result == PayResult.None) { _postponed.Add(current.Id); postponed++; }   // closed without a decision: asked again at the next start
                        break;
                    default:
                        _postponed.Add(current.Id);
                        postponed++;
                        break;
                }
            }
            return new ReminderOutcome(settled, paid, postponed, waiting);
        }
        finally
        {
            _busy = false;
        }
    }

    private static PaymentReminder Describe(PlannedPayment payment, Payee? payee, DateOnly today) => new(
        payment, payee?.Name ?? "?", BankAccount.Format(payee?.AccountNumber), payment.Title,
        payment.Amount.ToString("C2", System.Globalization.CultureInfo.CurrentCulture),
        payment.DueDate.ToString("dd.MM.yyyy ddd", System.Globalization.CultureInfo.CurrentCulture),
        today.DayNumber > payment.DueDate.DayNumber
            ? Res.Format(today.DayNumber - payment.DueDate.DayNumber == 1 ? "PayLateOne" : "PayLateDays", today.DayNumber - payment.DueDate.DayNumber)
            : payment.DueDate == today ? Res.Get("PayDueToday")
            : payment.DueDate.DayNumber - today.DayNumber == 1 ? Res.Get("PayDueTomorrow")
            : Res.Format("PayDueInDays", payment.DueDate.DayNumber - today.DayNumber),
        payment.DueDate < today);
}
