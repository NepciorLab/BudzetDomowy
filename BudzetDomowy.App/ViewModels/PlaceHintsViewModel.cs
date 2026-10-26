using System.Collections.ObjectModel;
using System.Globalization;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.Core;
using BudzetDomowy.Core.Services;
using BudzetDomowy.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.ViewModels;

/// <summary>One place of the suggestions list: its weight can be typed in the table.</summary>
public sealed partial class PlaceHintRow : ObservableObject
{
    private readonly Action<PlaceHintRow> _weightEdited;

    public string Name { get; }
    public bool FromHistory { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanReset))] private bool _weightChanged;
    [ObservableProperty] private int _weight;

    public string Source => Res.Get(FromHistory ? "PlacesFromHistory" : "PlacesAddedByHand");

    /// <summary>Only a place typed before can go back to the weight its history gives.</summary>
    public bool CanReset => WeightChanged && FromHistory;

    public PlaceHintRow(PlaceSuggestion s, Action<PlaceHintRow> weightEdited)
    {
        _weightEdited = weightEdited;
        Name = s.Name;
        FromHistory = s.FromHistory;
        _weightChanged = s.WeightChanged;
        _weight = s.Weight;
    }

    partial void OnWeightChanged(int value) => _weightEdited(this);
}

/// <summary>The settings tab that edits the database of the "Place" suggestions: add a place, remove it, change its weight.</summary>
public sealed partial class PlaceHintsViewModel : ObservableObject
{
    private readonly BudgetCore _core;
    private bool _reloading;

    public ObservableCollection<PlaceHintRow> Rows { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newWeight = "";
    [ObservableProperty] private string _minEntries = "3";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _message = "";

    public PlaceHintsViewModel(BudgetCore core)
    {
        _core = core;
        Reload();
    }

    partial void OnSearchChanged(string value) => Reload();

    public void Reload()
    {
        _reloading = true;
        try
        {
            var all = _core.PlaceSuggestions.All();
            var needle = PlaceSuggestionService.Normalize(Search);
            var shown = needle.Length == 0 ? all : all.Where(s => PlaceSuggestionService.Normalize(s.Name).Contains(needle, StringComparison.Ordinal)).ToList();
            Rows.Clear();
            foreach (var s in shown) Rows.Add(new PlaceHintRow(s, OnWeightEdited));
            Summary = Res.Format("PlacesCount", shown.Count, all.Count);
        }
        finally
        {
            _reloading = false;
        }
    }

    private void OnWeightEdited(PlaceHintRow row)
    {
        if (_reloading) return;
        Run(() =>
        {
            _core.PlaceSuggestions.SetWeight(row.Name, Math.Max(0, row.Weight));
            row.WeightChanged = true;
        }, reload: false);
    }

    [RelayCommand]
    private void Add()
    {
        var name = NewName.Trim();
        if (name.Length == 0)
        {
            Message = Res.Get("PlacesNameRequired");
            return;
        }

        var weight = 1;
        if (NewWeight.Trim().Length > 0 && (!int.TryParse(NewWeight.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out weight) || weight < 0))
        {
            Message = Res.Get("PlacesBadWeight");
            return;
        }

        Run(() =>
        {
            _core.PlaceSuggestions.Add(name, weight);
            NewName = "";
            NewWeight = "";
            Message = Res.Format("PlacesAdded", name);
        });
    }

    [RelayCommand]
    private void Import()
    {
        if (!int.TryParse(MinEntries.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var min) || min < 1)
        {
            Message = Res.Get("PlacesBadMin");
            return;
        }

        Run(() =>
        {
            var added = _core.PlaceSuggestions.Import(min);
            Message = added == 0 ? Res.Get("PlacesNothingToImport") : Res.Format("PlacesImported", added);
        });
    }

    /// <summary>Removes every chosen place at once (Delete in the table).</summary>
    public void RemoveMany(IReadOnlyCollection<PlaceHintRow> rows)
    {
        if (rows.Count == 0) return;
        Run(() =>
        {
            _core.PlaceSuggestions.Remove(rows.Select(r => r.Name).ToList());
            Message = rows.Count == 1 ? Res.Format("PlacesRemoved", rows.First().Name) : Res.Format("PlacesRemovedMany", rows.Count);
        });
    }

    [RelayCommand]
    private void Remove(PlaceHintRow? row)
    {
        if (row is null) return;
        Run(() =>
        {
            _core.PlaceSuggestions.Remove(row.Name);
            Message = Res.Format("PlacesRemoved", row.Name);
        });
    }

    [RelayCommand]
    private void ResetWeight(PlaceHintRow? row)
    {
        if (row is null) return;
        Run(() => _core.PlaceSuggestions.ResetWeight(row.Name));
    }

    private void Run(Action action, bool reload = true)
    {
        try
        {
            action();
            if (reload) Reload();
        }
        catch (UpdateRequiredException)
        {
            Message = Res.Get("UpdateRequiredShort");
        }
    }
}
