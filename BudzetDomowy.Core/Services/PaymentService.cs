using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Storage;
using BudzetDomowy.Core.Sync;

namespace BudzetDomowy.Core.Services;

/// <summary>One row typed when planning payments.</summary>
public sealed record NewPayment(DateOnly DueDate, string Title, decimal Amount);

/// <summary>One row of a series being edited: an existing payment (<see cref="Id"/>) or a new one (null).</summary>
public sealed record SeriesRow(Guid? Id, DateOnly DueDate, string Title, decimal Amount);

/// <summary>
/// Bills to pay. A bill stays on the list until it is paid: paid now (booked with today's date), or set up in the bank for later
/// ("scheduled": no more reminders, and once its date has passed it counts as paid on that date). Paying books an expense
/// whose id comes from the payment, so two computers settling the same bill make the same transaction.
/// </summary>
public sealed class PaymentService
{
    /// <summary>How many days before the due date the reminders start.</summary>
    public const int ReminderDays = 3;

    private readonly EventRecorder _recorder;
    private readonly LocalReader _reader;
    private readonly TransactionService _transactions;

    public PaymentService(EventRecorder recorder, LocalReader reader, TransactionService transactions)
    {
        _recorder = recorder;
        _reader = reader;
        _transactions = transactions;
    }

    // ----- payees -----

    public Payee AddPayee(string name, string account, Guid? categoryId = null)
    {
        var payee = Validate(new Payee { Id = Guid.NewGuid(), Name = name, AccountNumber = account, CategoryId = categoryId });
        _recorder.Save(payee);
        return payee;
    }

    public Payee SavePayee(Payee payee)
    {
        var valid = Validate(payee);
        _recorder.Save(valid);
        return valid;
    }

    private static Payee Validate(Payee payee)
    {
        var name = payee.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("The payee needs a name.", nameof(payee));
        if (!BankAccount.TryParseValid(payee.AccountNumber, out var digits)) throw new ArgumentException("The account number is not correct.", nameof(payee));
        return payee with { Name = name, AccountNumber = digits };
    }

    /// <summary>Removes a payee that has no payments on the list; false when it is still in use.</summary>
    public bool DeletePayee(Guid id)
    {
        if (_reader.GetPlannedPayments().Any(p => p.PayeeId == id)) return false;
        _recorder.Delete(EntityTypes.Payee, id);
        return true;
    }

    // ----- payments -----

    /// <summary>Adds the typed rows as one series (they are edited together later).</summary>
    public IReadOnlyList<PlannedPayment> AddPayments(Guid payeeId, IEnumerable<NewPayment> rows)
    {
        if (_reader.GetPayees().All(p => p.Id != payeeId)) throw new ArgumentException("Unknown payee.", nameof(payeeId));
        var seriesId = Guid.NewGuid();
        var added = new List<PlannedPayment>();
        foreach (var row in rows)
        {
            if (row.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(rows), "The amount of a payment must be positive.");
            var payment = new PlannedPayment
            {
                Id = Guid.NewGuid(), PayeeId = payeeId, DueDate = row.DueDate, Title = row.Title.Trim(), Amount = row.Amount, Status = PaymentStatus.Pending, SeriesId = seriesId,
            };
            _recorder.Save(payment);
            added.Add(payment);
        }
        return added;
    }

    public void DeletePayment(Guid id) => _recorder.Delete(EntityTypes.PlannedPayment, id);

    /// <summary>The payments typed together with this one (it alone when it has no series), oldest date first; empty when it is no longer on the list.</summary>
    public IReadOnlyList<PlannedPayment> SeriesOf(Guid paymentId)
    {
        var all = _reader.GetPlannedPayments();
        if (all.FirstOrDefault(p => p.Id == paymentId) is not { } payment) return [];
        return payment.SeriesId is { } series ? all.Where(p => p.SeriesId == series).ToList() : [payment];
    }

    /// <summary>
    /// Saves a series after editing: rows with an id update those payments, rows without one are added, and payments of the series
    /// that are not in <paramref name="rows"/> are removed. A payment whose payee, date, title or amount changed goes back to "to pay"
    /// (what was set up in the bank no longer matches); an unchanged one keeps its state. A row whose payment was paid or removed
    /// in the meantime (e.g. on the other computer) is not brought back.
    /// </summary>
    public IReadOnlyList<PlannedPayment> SaveSeries(Guid anyPaymentId, Guid payeeId, IEnumerable<SeriesRow> rows)
    {
        if (_reader.GetPayees().All(p => p.Id != payeeId)) throw new ArgumentException("Unknown payee.", nameof(payeeId));
        var existing = SeriesOf(anyPaymentId).ToDictionary(p => p.Id);
        var seriesId = existing.Values.Select(p => p.SeriesId).FirstOrDefault(s => s is not null) ?? Guid.NewGuid();
        var kept = new HashSet<Guid>();
        var result = new List<PlannedPayment>();
        foreach (var row in rows)
        {
            if (row.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(rows), "The amount of a payment must be positive.");
            var title = row.Title.Trim();
            if (row.Id is { } id)
            {
                if (!existing.TryGetValue(id, out var old)) continue;
                kept.Add(id);
                var changed = old.PayeeId != payeeId || old.DueDate != row.DueDate || old.Title != title || old.Amount != row.Amount;
                var updated = old with
                {
                    PayeeId = payeeId, DueDate = row.DueDate, Title = title, Amount = row.Amount, SeriesId = seriesId,
                    Status = changed ? PaymentStatus.Pending : old.Status,
                };
                if (updated != old) _recorder.Save(updated);
                result.Add(updated);
            }
            else
            {
                var added = new PlannedPayment
                {
                    Id = Guid.NewGuid(), PayeeId = payeeId, DueDate = row.DueDate, Title = title, Amount = row.Amount,
                    Status = PaymentStatus.Pending, SeriesId = seriesId,
                };
                _recorder.Save(added);
                result.Add(added);
            }
        }

        foreach (var old in existing.Values.Where(p => !kept.Contains(p.Id))) _recorder.Delete(EntityTypes.PlannedPayment, old.Id);
        return result;
    }

    /// <summary>The transfer is set up in the bank for a later day: no reminders from now on.</summary>
    public void MarkScheduled(Guid id)
    {
        if (_reader.GetPlannedPayments().FirstOrDefault(p => p.Id == id) is { } payment && payment.Status != PaymentStatus.Scheduled)
            _recorder.Save(payment with { Status = PaymentStatus.Scheduled });
    }

    /// <summary>Back to "to pay" (the reminders return).</summary>
    public void MarkPending(Guid id)
    {
        if (_reader.GetPlannedPayments().FirstOrDefault(p => p.Id == id) is { } payment && payment.Status != PaymentStatus.Pending)
            _recorder.Save(payment with { Status = PaymentStatus.Pending });
    }

    /// <summary>Paid: booked as an expense on <paramref name="date"/> (today when not given) and taken off the list. Null when there is no such payment.</summary>
    public Transaction? MarkPaid(Guid id, DateOnly? date = null)
    {
        var payment = _reader.GetPlannedPayments().FirstOrDefault(p => p.Id == id);
        if (payment is null) return null;
        var payee = _reader.GetPayees().FirstOrDefault(p => p.Id == payment.PayeeId);

        var transactionId = TransactionIdOf(id);
        var existing = _reader.GetTransaction(transactionId);
        if (existing is null && !_reader.IsTombstone(EntityTypes.Transaction, transactionId))
        {
            existing = _transactions.Add(date ?? DateOnly.FromDateTime(DateTime.Today), TransactionType.Expense, payment.Amount, CategoryFor(payee),
                payment.Title, null, place: payee?.Name, id: transactionId);
        }
        _recorder.Delete(EntityTypes.PlannedPayment, id);
        return existing;
    }

    public static Guid TransactionIdOf(Guid paymentId) => DeterministicId.FromString("planned-payment:" + paymentId.ToString("D"));

    /// <summary>The payee's own category, otherwise the one its name was most often booked under before.</summary>
    private Guid? CategoryFor(Payee? payee)
    {
        if (payee is null) return null;
        var categories = _reader.GetCategories();
        if (payee.CategoryId is { } own && categories.Any(c => c.Id == own)) return own;
        var suggestion = PlaceCategoryDictionary.Build(_reader.GetTransactions(), TransactionType.Expense).Suggest(payee.Name).FirstOrDefault();
        return suggestion == Guid.Empty ? null : suggestion;
    }

    /// <summary>
    /// Scheduled payments whose date has passed count as paid on their date: they are booked and taken off the list.
    /// Returns how many were settled.
    /// </summary>
    public int SettleScheduled(DateOnly today)
    {
        var settled = 0;
        foreach (var payment in _reader.GetPlannedPayments().Where(p => p.Status == PaymentStatus.Scheduled && p.DueDate < today).ToList())
        {
            MarkPaid(payment.Id, payment.DueDate);
            settled++;
        }
        return settled;
    }

    /// <summary>Payments to remind about: still to be paid, due within <see cref="ReminderDays"/> days or already late. Oldest first.</summary>
    public IReadOnlyList<PlannedPayment> DueForReminder(DateOnly today) =>
        _reader.GetPlannedPayments().Where(p => p.Status == PaymentStatus.Pending && p.DueDate <= today.AddDays(ReminderDays))
            .OrderBy(p => p.DueDate).ThenBy(p => p.Title).ToList();
}
