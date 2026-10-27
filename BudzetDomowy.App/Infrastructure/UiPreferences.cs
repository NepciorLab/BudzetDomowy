using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Display preferences that views read directly (auto column width, hidden columns, scale, category symbol, amount style).
/// Backed by the local settings file; a single instance because XAML binds to it statically.
/// </summary>
public sealed partial class UiPreferences : ObservableObject
{
    public static UiPreferences Instance { get; } = new();

    private SettingsStore? _store;
    private bool _loading;

    [ObservableProperty] private bool _autoFitColumns;
    [ObservableProperty] private UiScaleChoice _scale = UiScaleChoice.Medium;
    [ObservableProperty] private MarkerShape _marker = MarkerShape.Circle;
    [ObservableProperty] private bool _showMinus = true;
    [ObservableProperty] private bool _colorAmounts = true;
    [ObservableProperty] private bool _showClock = true;
    [ObservableProperty] private bool _subcategoriesInheritColor;

    /// <summary>Raised when scale, symbol or amount style changed (screens that format text refresh themselves).</summary>
    public event EventHandler? AppearanceChanged;

    private readonly List<(WeakReference Target, Action<object> Action)> _listeners = [];

    /// <summary>
    /// Calls <paramref name="action"/> on <paramref name="target"/> whenever the appearance changes, without keeping the target alive
    /// (screens come and go; this object lives as long as the program).
    /// </summary>
    public void OnAppearanceChanged<T>(T target, Action<T> action) where T : class =>
        _listeners.Add((new WeakReference(target), o => action((T)o)));

    private void RaiseAppearanceChanged()
    {
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        _listeners.RemoveAll(l => !l.Target.IsAlive);
        foreach (var (target, action) in _listeners.ToList())
        {
            if (target.Target is not { } alive) continue;
            try { action(alive); }
            catch (Exception e) { AppLog.Warn("Refreshing a screen after an appearance change failed: " + e.Message); }
        }
    }

    public double ScaleFactor => FactorOf(Scale);

    public static double FactorOf(UiScaleChoice scale) => scale switch
    {
        UiScaleChoice.Small => 0.9,
        UiScaleChoice.Large => 1.2,
        _ => 1.0,
    };

    public void Initialize(SettingsStore store)
    {
        _store = store;
        _loading = true;
        try
        {
            var s = store.Current;
            AutoFitColumns = s.AutoFitColumns;
            Scale = s.UiScale;
            Marker = s.CategoryMarker;
            ShowMinus = s.ShowMinus;
            ColorAmounts = s.ColorAmounts;
            ShowClock = s.ShowClock;
            SubcategoriesInheritColor = s.SubcategoriesInheritColor;
        }
        finally
        {
            _loading = false;
        }
        RaiseAppearanceChanged();
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_loading && _store is { } s) s.Update(change);
    }

    private void Appearance()
    {
        if (!_loading) RaiseAppearanceChanged();
    }

    partial void OnAutoFitColumnsChanged(bool value) => Save(x => x.AutoFitColumns == value ? x : x with { AutoFitColumns = value });

    partial void OnScaleChanged(UiScaleChoice value)
    {
        OnPropertyChanged(nameof(ScaleFactor));
        Save(x => x with { UiScale = value });
        Appearance();
    }

    partial void OnMarkerChanged(MarkerShape value)
    {
        Save(x => x with { CategoryMarker = value });
        Appearance();
    }

    partial void OnShowMinusChanged(bool value)
    {
        Save(x => x with { ShowMinus = value });
        Appearance();
    }

    partial void OnShowClockChanged(bool value) => Save(x => x with { ShowClock = value });

    partial void OnSubcategoriesInheritColorChanged(bool value) => Save(x => x with { SubcategoriesInheritColor = value });

    partial void OnColorAmountsChanged(bool value)
    {
        Save(x => x with { ColorAmounts = value });
        Appearance();
    }

    /// <summary>"+" for income and "−" for an expense, or nothing at all when the user turned the signs off.</summary>
    public string SignOf(bool isIncome) => !ShowMinus ? "" : isIncome ? "+" : "−";

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
