using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core.Domain;
using BudzetDomowy.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.ViewModels;

public sealed record TypeChoice(bool IsIncome, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One editable row of the transaction table. Every property setter saves the change immediately.</summary>
public sealed class TransactionRowViewModel : ObservableObject
{
    private readonly TransactionsViewModel _owner;
    private IReadOnlyList<CategoryOption>? _choices;
    private int _choicesVersion = -1;
    private TransactionType _choicesType;

    public Transaction Model { get; private set; }

    public TransactionRowViewModel(TransactionsViewModel owner, Transaction model)
    {
        _owner = owner;
        Model = model;
    }

    public Guid Id => Model.Id;

    public DateTime? Date
    {
        get => Model.Date.ToDateTime(TimeOnly.MinValue);
        set
        {
            if (value is not { } v) return;
            var d = DateOnly.FromDateTime(v);
            if (d != Model.Date) _owner.Edit(this, t => t with { Date = d });
        }
    }

    public bool IsIncome => Model.Type == TransactionType.Income;

    public TypeChoice TypeChoice
    {
        get => IsIncome ? TransactionsViewModel.IncomeChoice : TransactionsViewModel.ExpenseChoice;
        set
        {
            if (value is null || value.IsIncome == IsIncome) return;
            _owner.Edit(this, t => t with { Type = value.IsIncome ? TransactionType.Income : TransactionType.Expense });
        }
    }

    public string AmountText
    {
        get => Model.Amount.ToString("N2", CultureInfo.CurrentCulture);
        set
        {
            if (!AmountParser.TryParse(value, out var amount) || amount <= 0) return;
            if (amount != Model.Amount) _owner.Edit(this, t => t with { Amount = amount });
        }
    }

    public string AmountDisplay =>
        (IsIncome ? "+" : "−") + Model.AmountPln.ToString("C2", CultureInfo.CurrentCulture);

    public string Place
    {
        get => Model.Place;
        set
        {
            var v = (value ?? "").Trim();
            if (v == Model.Place) return;
            _owner.Edit(this, t => t with { Place = v });
            if (Model.CategoryId is { } c) _owner.LearnRule(v, c);
        }
    }

    public Guid? CategoryId
    {
        get => Model.CategoryId;
        set
        {
            if (value == Model.CategoryId) return;
            _owner.Edit(this, t => t with { CategoryId = value });
            if (value is { } c) _owner.LearnRule(Model.Place, c);
        }
    }

    public string CategoryPath => _owner.CategoryPath(Model.CategoryId);
    public string CategoryColor => _owner.CategoryColor(Model.CategoryId);
    public string CategoryIcon => _owner.CategoryIcon(Model.CategoryId);

    public string Note
    {
        get => Model.Note;
        set
        {
            var v = (value ?? "").Trim();
            if (v != Model.Note) _owner.Edit(this, t => t with { Note = v });
        }
    }

    public Guid EventKey
    {
        get => Model.EventId ?? Guid.Empty;
        set
        {
            var id = value == Guid.Empty ? (Guid?)null : value;
            if (id != Model.EventId) _owner.Edit(this, t => t with { EventId = id });
        }
    }

    public string EventName => _owner.EventName(Model.EventId);

    public IReadOnlyList<EventOption> EventChoices => _owner.EventChoices;

    /// <summary>Categories valid for this row's type; the current category is always included so it can be shown.</summary>
    public IReadOnlyList<CategoryOption> Choices
    {
        get
        {
            // Keep the same list instance while editing so the combo box is not rebuilt under the user's hands.
            var currentMissing = _choices is not null && Model.CategoryId is { } id && _choices.All(o => o.Id != id);
            if (_choices is null || _choicesVersion != _owner.ChoicesVersion || _choicesType != Model.Type || currentMissing)
            {
                _choices = _owner.ChoicesFor(Model.Type, Model.CategoryId);
                _choicesVersion = _owner.ChoicesVersion;
                _choicesType = Model.Type;
            }
            return _choices;
        }
    }

    /// <summary>Takes a newer version of the transaction; refreshes the bindings only if something visible changed.</summary>
    public void Refresh(Transaction t, bool force = false)
    {
        var changed = force || !SameVisible(Model, t);
        Model = t;
        if (changed) OnPropertyChanged(string.Empty);
    }

    private static bool SameVisible(Transaction a, Transaction b) =>
        a.Date == b.Date && a.Type == b.Type && a.Amount == b.Amount && a.AmountPln == b.AmountPln
        && a.CurrencyCode == b.CurrencyCode && a.CategoryId == b.CategoryId && a.Place == b.Place && a.Note == b.Note && a.EventId == b.EventId;
}
