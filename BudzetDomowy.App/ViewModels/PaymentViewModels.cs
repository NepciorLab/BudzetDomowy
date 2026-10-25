using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

public enum PayResult { None, Paid, Scheduled }

/// <summary>The windows of the payments (opened through this so that tests need no window).</summary>
public interface IPaymentDialogs
{
    /// <summary>The window to type the bills of one payee.</summary>
    void OpenEntry();

    /// <summary>The list of payees.</summary>
    void ManagePayees();

    /// <summary>Adds (null) or edits a payee; null when cancelled.</summary>
    Payee? EditPayee(Payee? existing);

    /// <summary>The QR code of a payment with the choice "paid" / "scheduled".</summary>
    PayResult ShowPay(PlannedPayment payment);
}

internal static class PaymentText
{
    public static string Money(decimal value) => value.ToString("C2", CultureInfo.CurrentCulture);

    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy ddd", CultureInfo.CurrentCulture);

    /// <summary>"in 3 days", "today", "2 days late".</summary>
    public static string Relative(DateOnly due, DateOnly today)
    {
        var days = due.DayNumber - today.DayNumber;
        return days switch
        {
            0 => Res.Get("PayDueToday"),
            1 => Res.Get("PayDueTomorrow"),
            > 1 => Res.Format("PayDueInDays", days),
            -1 => Res.Get("PayLateOne"),
            _ => Res.Format("PayLateDays", -days),
        };
    }
}

// ================================================================ the list
public sealed partial class PaymentItemViewModel : ObservableObject
{
    public PlannedPayment Payment { get; }
    public string PayeeName { get; }
    public string Account { get; }

    public PaymentItemViewModel(PlannedPayment payment, Payee? payee, DateOnly today)
    {
        Payment = payment;
        PayeeName = payee?.Name ?? "?";
        Account = BankAccount.Format(payee?.AccountNumber);
        DueText = PaymentText.Date(payment.DueDate);
        Relative = PaymentText.Relative(payment.DueDate, today);
        IsLate = payment.DueDate < today;
        IsScheduled = payment.Status == PaymentStatus.Scheduled;
    }

    public Guid Id => Payment.Id;
    public string Title => Payment.Title;
    public string AmountText => PaymentText.Money(Payment.Amount);
    public string DueText { get; }
    public string Relative { get; }
    public bool IsLate { get; }
    public bool IsScheduled { get; }
    public string StatusText => Res.Get(IsScheduled ? "PayStatusScheduled" : "PayStatusPending");
}

/// <summary>The "Payments" page: bills still to be paid, with the button that shows the QR code.</summary>
public sealed partial class PaymentsViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly IPaymentDialogs _dialogs;
    private readonly IMessageDialogs _messages;
    private readonly ToastService _toast;

    public ObservableCollection<PaymentItemViewModel> Items { get; } = [];

    [ObservableProperty] private PaymentItemViewModel? _selected;
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string _summary = "";

    public PaymentsViewModel(BudgetCore core, DataHub hub, IPaymentDialogs dialogs, IMessageDialogs messages, ToastService toast)
    {
        _core = core;
        _dialogs = dialogs;
        _messages = messages;
        _toast = toast;
        hub.Changed += (_, _) => Reload();
        Reload();
    }

    public void Reload()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var keep = Selected?.Id;
        var payees = _core.Reader.GetPayees().ToDictionary(p => p.Id);
        Items.Clear();
        foreach (var payment in _core.Reader.GetPlannedPayments())
            Items.Add(new PaymentItemViewModel(payment, payees.GetValueOrDefault(payment.PayeeId), today));
        Selected = keep is { } id ? Items.FirstOrDefault(i => i.Id == id) : null;
        HasItems = Items.Count > 0;

        var pending = Items.Where(i => !i.IsScheduled).ToList();
        Summary = Items.Count == 0
            ? ""
            : Res.Format("PaySummary", pending.Count, PaymentText.Money(pending.Sum(i => i.Payment.Amount)), Items.Count - pending.Count);
    }

    [RelayCommand]
    private void AddPayments()
    {
        _dialogs.OpenEntry();
        Reload();
    }

    [RelayCommand]
    private void ManagePayees()
    {
        _dialogs.ManagePayees();
        Reload();
    }

    [RelayCommand]
    private void Pay(PaymentItemViewModel? item)
    {
        if (item is null) return;
        _dialogs.ShowPay(item.Payment);
        Reload();
    }

    [RelayCommand]
    private void Delete(PaymentItemViewModel? item)
    {
        if (item is null) return;
        if (!_messages.Confirm(Res.Get("PayDeleteTitle"), Res.Format("PayDeleteQuestion", item.PayeeName, item.Title, item.AmountText))) return;
        try
        {
            _core.Payments.DeletePayment(item.Id);
            _toast.Show(Res.Get("PayDeleted"));
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
        }
        Reload();
    }
}

// ================================================================ payees
public sealed partial class PayeeEditViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly Payee? _existing;

    public IReadOnlyList<CategoryOption> Categories { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Guid? _categoryId;
    [ObservableProperty] private string _errorMessage = "";

    private string _accountText = "";

    /// <summary>What is typed in the account box; the box shows it in the standard groups once it is left.</summary>
    public string AccountText
    {
        get => _accountText;
        set
        {
            value ??= "";
            if (value == _accountText) return;
            _accountText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccountHint));
            OnPropertyChanged(nameof(AccountState));
        }
    }

    public string Title => Res.Get(_existing is null ? "PayeeNew" : "PayeeEdit");

    /// <summary>"correct", "wrong check number", "26 digits are needed (n so far)", "only digits..."</summary>
    public string AccountHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AccountText)) return "";
            if (BankAccount.TryNormalize(AccountText, out var digits)) return BankAccount.IsValid(digits) ? Res.Get("AccountOk") : Res.Get("AccountBadCheck");
            return HasForeignCharacters(AccountText) ? Res.Get("AccountBadChars") : Res.Format("AccountLength", digits.Length, BankAccount.Length);
        }
    }

    /// <summary>Anything but digits, spaces, dashes and a leading "PL".</summary>
    private static bool HasForeignCharacters(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("PL", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return t.Any(c => !char.IsDigit(c) && c is not (' ' or '-' or ' '));
    }

    /// <summary>"Ok", "Bad", or "None" while nothing is typed (the hint is coloured by it).</summary>
    public string AccountState => string.IsNullOrWhiteSpace(AccountText) ? "None" : BankAccount.TryParseValid(AccountText, out _) ? "Ok" : "Bad";

    public Payee? Result { get; private set; }

    public event Action? Saved;

    public PayeeEditViewModel(BudgetCore core, Payee? existing)
    {
        _core = core;
        _existing = existing;
        Categories = CategoryHelpers.ForEntry(core.Reader.GetCategories(), TransactionType.Expense);
        if (existing is not null)
        {
            _name = existing.Name;
            _accountText = BankAccount.Format(existing.AccountNumber);
            _categoryId = existing.CategoryId;
        }
    }

    /// <summary>Shows the number in the standard groups (called when the box is left).</summary>
    public void FormatAccount()
    {
        BankAccount.TryNormalize(AccountText, out var digits);
        if (!HasForeignCharacters(AccountText) && digits.Length > 0) AccountText = BankAccount.Format(digits);
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = "";
        FormatAccount();
        if (Name.Trim().Length == 0)
        {
            ErrorMessage = Res.Get("PayeeNameRequired");
            return;
        }
        if (!BankAccount.TryParseValid(AccountText, out var digits))
        {
            ErrorMessage = string.IsNullOrWhiteSpace(AccountText) ? Res.Get("PayeeAccountRequired") : Res.Get("PayeeAccountWrong");
            return;
        }

        try
        {
            Result = _existing is null
                ? _core.Payments.AddPayee(Name, digits, CategoryId)
                : _core.Payments.SavePayee(_existing with { Name = Name, AccountNumber = digits, CategoryId = CategoryId });
        }
        catch (UpdateRequiredException)
        {
            ErrorMessage = Res.Get("UpdateRequiredShort");
            return;
        }
        Saved?.Invoke();
    }
}

public sealed record PayeeRow(Payee Payee, string AccountText);

/// <summary>The list of payees with add, edit and delete.</summary>
public sealed partial class PayeesViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly IPaymentDialogs _dialogs;
    private readonly IMessageDialogs _messages;

    public ObservableCollection<PayeeRow> Items { get; } = [];

    [ObservableProperty] private PayeeRow? _selected;
    [ObservableProperty] private string _message = "";

    public PayeesViewModel(BudgetCore core, IPaymentDialogs dialogs, IMessageDialogs messages)
    {
        _core = core;
        _dialogs = dialogs;
        _messages = messages;
        Reload();
    }

    public void Reload()
    {
        var keep = Selected?.Payee.Id;
        Items.Clear();
        foreach (var p in _core.Reader.GetPayees()) Items.Add(new PayeeRow(p, BankAccount.Format(p.AccountNumber)));
        Selected = keep is { } id ? Items.FirstOrDefault(i => i.Payee.Id == id) : null;
    }

    [RelayCommand]
    private void Add()
    {
        if (_dialogs.EditPayee(null) is { } added)
        {
            Reload();
            Selected = Items.FirstOrDefault(i => i.Payee.Id == added.Id);
        }
    }

    [RelayCommand]
    private void Edit(PayeeRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (_dialogs.EditPayee(row.Payee) is not null) Reload();
    }

    [RelayCommand]
    private void Delete(PayeeRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        Message = "";
        if (!_messages.Confirm(Res.Get("PayeeDeleteTitle"), Res.Format("PayeeDeleteQuestion", row.Payee.Name))) return;
        if (!_core.Payments.DeletePayee(row.Payee.Id))
        {
            Message = Res.Format("PayeeInUse", row.Payee.Name);
            return;
        }
        Reload();
    }
}

// ================================================================ typing the bills of one payee
public sealed partial class PaymentRowViewModel : ObservableObject
{
    private readonly PaymentEntryViewModel _owner;
    private string _amountExpression = "";

    public PaymentRowViewModel(PaymentEntryViewModel owner, DateTime? due)
    {
        _owner = owner;
        _dueDate = due;
    }

    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private decimal? _value;
    [ObservableProperty] private string _display = "";
    [ObservableProperty] private bool _isInvalid;

    /// <summary>A number or a calculation; the box shows the result after Enter.</summary>
    public string AmountExpression
    {
        get => _amountExpression;
        set
        {
            value ??= "";
            if (value == _amountExpression) return;
            _amountExpression = value;
            OnPropertyChanged();
            Recalculate();
        }
    }

    public int Number => _owner.Rows.IndexOf(this) + 1;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(AmountExpression);

    public void Recalculate()
    {
        Value = null;
        Display = "";
        IsInvalid = false;
        if (string.IsNullOrWhiteSpace(_amountExpression)) return;
        if (AmountExpression.Length > 0 && BudzetDomowy.Core.Services.AmountExpression.TryEvaluate(_amountExpression, out var v) && v > 0)
        {
            Value = v;
            if (!BudzetDomowy.Core.Services.AmountExpression.IsPlainAmount(_amountExpression, BudzetDomowy.Core.Currency.CurrencyCatalog.Codes))
                Display = v.ToString("0.##", CultureInfo.CurrentCulture);
        }
        else
        {
            IsInvalid = true;
        }
    }

    internal void Renumber() => OnPropertyChanged(nameof(Number));
}

public enum PaymentFocus { Date, Amount, Confirm }

public sealed record PaymentEntryFocus(PaymentFocus Target, PaymentRowViewModel? Row);

/// <summary>
/// Planning the bills of one payee: the payee is chosen (or added), then each row has a date, a title and an amount. Enter in the
/// amount accepts it and opens the next row; the empty last row is ignored when saving.
/// </summary>
public sealed partial class PaymentEntryViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly IPaymentDialogs _dialogs;
    private readonly ToastService _toast;

    public ObservableCollection<Payee> Payees { get; } = [];
    public ObservableCollection<PaymentRowViewModel> Rows { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PayeeAccount))] private Payee? _selectedPayee;
    [ObservableProperty] private string _errorMessage = "";

    public string PayeeAccount => SelectedPayee is null ? "" : BankAccount.Format(SelectedPayee.AccountNumber);

    public event Action? Saved;

    public PaymentEntryViewModel(BudgetCore core, IPaymentDialogs dialogs, ToastService toast)
    {
        _core = core;
        _dialogs = dialogs;
        _toast = toast;
        ReloadPayees();
        SelectedPayee = Payees.Count == 1 ? Payees[0] : null;
        Rows.Add(new PaymentRowViewModel(this, DateTime.Today));
    }

    private void ReloadPayees()
    {
        var keep = SelectedPayee?.Id;
        Payees.Clear();
        foreach (var p in _core.Reader.GetPayees()) Payees.Add(p);
        SelectedPayee = keep is { } id ? Payees.FirstOrDefault(p => p.Id == id) : SelectedPayee;
    }

    [RelayCommand]
    private void NewPayee()
    {
        if (_dialogs.EditPayee(null) is not { } payee) return;
        ReloadPayees();
        SelectedPayee = Payees.FirstOrDefault(p => p.Id == payee.Id);
    }

    [RelayCommand]
    private void AddRow() => NewRow();

    [RelayCommand]
    private void RemoveRow(PaymentRowViewModel? row)
    {
        if (row is null || Rows.Count <= 1) return;
        Rows.Remove(row);
        foreach (var r in Rows) r.Renumber();
    }

    private PaymentRowViewModel NewRow()
    {
        // the next bill of the same payee usually has the same date, or a later one: it starts as the previous
        var row = new PaymentRowViewModel(this, Rows.LastOrDefault()?.DueDate ?? DateTime.Today);
        Rows.Add(row);
        foreach (var r in Rows) r.Renumber();
        return row;
    }

    /// <summary>
    /// Enter in an amount: it is calculated; a good amount in the last row opens the next row (focus on its date); in another row the
    /// focus goes to the next row. An empty last row sends the keyboard to the confirm button.
    /// </summary>
    public PaymentEntryFocus CommitRow(PaymentRowViewModel row)
    {
        row.Recalculate();
        ErrorMessage = "";
        if (row.IsInvalid)
        {
            ErrorMessage = Res.Format("PayRowBadAmount", row.Number);
            return new PaymentEntryFocus(PaymentFocus.Amount, row);
        }
        if (row.IsEmpty) return new PaymentEntryFocus(PaymentFocus.Confirm, null);
        if (row.Value is null)
        {
            ErrorMessage = Res.Format("PayRowBadAmount", row.Number);
            return new PaymentEntryFocus(PaymentFocus.Amount, row);
        }

        var index = Rows.IndexOf(row);
        var next = index == Rows.Count - 1 ? NewRow() : Rows[index + 1];
        return new PaymentEntryFocus(PaymentFocus.Date, next);
    }

    /// <summary>Saves all typed rows as bills of the chosen payee. False (with a message) when something is missing.</summary>
    public bool Confirm()
    {
        ErrorMessage = "";
        foreach (var r in Rows) r.Recalculate();
        if (SelectedPayee is null)
        {
            ErrorMessage = Res.Get("PayChoosePayee");
            return false;
        }

        var used = Rows.Where(r => !r.IsEmpty).ToList();
        if (used.Count == 0)
        {
            ErrorMessage = Res.Get("PayNothingTyped");
            return false;
        }
        foreach (var row in used)
        {
            if (row.IsInvalid || row.Value is not > 0)
            {
                ErrorMessage = Res.Format("PayRowBadAmount", row.Number);
                return false;
            }
            if (row.DueDate is null)
            {
                ErrorMessage = Res.Format("PayRowNoDate", row.Number);
                return false;
            }
        }

        try
        {
            _core.Payments.AddPayments(SelectedPayee.Id, used.Select(r => new NewPayment(DateOnly.FromDateTime(r.DueDate!.Value), r.Title, r.Value!.Value)));
        }
        catch (UpdateRequiredException)
        {
            ErrorMessage = Res.Get("UpdateRequiredShort");
            return false;
        }

        _toast.Show(Res.Format("PayAdded", used.Count, SelectedPayee.Name));
        Saved?.Invoke();
        return true;
    }
}

// ================================================================ paying: the QR code
public sealed partial class PaymentPayViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private readonly ToastService _toast;

    public PlannedPayment Payment { get; }
    public Payee? Payee { get; }
    public string PayeeName => Payee?.Name ?? "?";
    public string AccountFormatted => BankAccount.Format(Payee?.AccountNumber);
    public string Title => Payment.Title;
    public string AmountText => PaymentText.Money(Payment.Amount);
    public string DueText => PaymentText.Date(Payment.DueDate);
    public string Relative => PaymentText.Relative(Payment.DueDate, DateOnly.FromDateTime(DateTime.Today));
    public bool IsScheduled => Payment.Status == PaymentStatus.Scheduled;

    /// <summary>What the QR code holds (the text of the recommendation).</summary>
    public string QrText { get; }

    /// <summary>A remark about the code: shortened texts, or an amount that does not fit in it.</summary>
    public string QrNote { get; }

    public System.Windows.Media.Imaging.BitmapSource? Qr { get; }

    public PayResult Result { get; private set; }

    public event Action? Closed;

    public PaymentPayViewModel(BudgetCore core, ToastService toast, PlannedPayment payment)
    {
        _core = core;
        _toast = toast;
        Payment = payment;
        Payee = core.Reader.GetPayees().FirstOrDefault(p => p.Id == payment.PayeeId);

        var payload = PaymentQr.Build(Payee?.AccountNumber ?? "", PayeeName, payment.Title, payment.Amount);
        QrText = payload.Text;
        QrNote = payload.AmountOmitted ? Res.Get("QrAmountOmitted") : payload.Truncated ? Res.Get("QrShortened") : "";
        Qr = Payee is null ? null : QrImage.Create(payload.Text);
    }

    [RelayCommand]
    private void MarkPaid()
    {
        try
        {
            if (_core.Payments.MarkPaid(Payment.Id) is { } booked)
                _toast.Show(Res.Format("PayPaidToast", booked.Amount.ToString("C2", CultureInfo.CurrentCulture), PayeeName));
        }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
            return;
        }
        Result = PayResult.Paid;
        Closed?.Invoke();
    }

    [RelayCommand]
    private void MarkScheduled()
    {
        try { _core.Payments.MarkScheduled(Payment.Id); }
        catch (UpdateRequiredException)
        {
            _toast.Show(Res.Get("UpdateRequiredShort"));
            return;
        }
        Result = PayResult.Scheduled;
        Closed?.Invoke();
    }

    [RelayCommand]
    private void BackToPending()
    {
        _core.Payments.MarkPending(Payment.Id);
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    [RelayCommand]
    private void CopyAccount() => Copy(Payee?.AccountNumber ?? "");

    [RelayCommand]
    private void CopyTitle() => Copy(Payment.Title);

    [RelayCommand]
    private void CopyAmount() => Copy(Payment.Amount.ToString("0.00", CultureInfo.CurrentCulture));

    private static void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is busy: nothing is lost */ }
    }
}
