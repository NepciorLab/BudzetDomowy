using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Display preferences that tables read directly (auto column width, hidden columns). Backed by the local settings file;
/// a single instance because XAML binds to it statically.
/// </summary>
public sealed partial class UiPreferences : ObservableObject
{
    public static UiPreferences Instance { get; } = new();

    private SettingsStore? _store;

    [ObservableProperty] private bool _autoFitColumns;

    public void Initialize(SettingsStore store)
    {
        _store = store;
        AutoFitColumns = store.Current.AutoFitColumns;
    }

    partial void OnAutoFitColumnsChanged(bool value)
    {
        if (_store is { } s && s.Current.AutoFitColumns != value) s.Update(x => x with { AutoFitColumns = value });
    }

    /// <summary>Indexes of the columns of a table the user chose to hide.</summary>
    public IReadOnlyList<int> HiddenColumns(string tableKey) =>
        _store?.Current.HiddenColumns.TryGetValue(tableKey, out var list) == true ? list : [];

    public void SetHiddenColumns(string tableKey, IEnumerable<int> hidden)
    {
        if (_store is not { } s) return;
        var copy = s.Current.HiddenColumns.ToDictionary(kv => kv.Key, kv => kv.Value);
        var list = hidden.Distinct().OrderBy(i => i).ToList();
        if (list.Count == 0) copy.Remove(tableKey); else copy[tableKey] = list;
        s.Update(x => x with { HiddenColumns = copy });
    }
}
