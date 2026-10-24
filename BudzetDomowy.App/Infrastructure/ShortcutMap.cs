using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BudzetDomowy.App.Infrastructure;

/// <param name="Id">Stable name of the command (kept in the settings file).</param>
/// <param name="Default">Keys as text, e.g. "Ctrl+K".</param>
public sealed record ShortcutDef(string Id, string Default, string DescriptionKey, string? Argument = null);

/// <summary>
/// The keys of the commands that can be changed in Settings. Defaults are listed here; a changed key is kept in the local settings file.
/// Keys are written like "Ctrl+Alt+K" (modifiers Ctrl, Alt, Shift, Win in this order, then the key).
/// </summary>
public sealed class ShortcutMap
{
    public static ShortcutMap Instance { get; } = new();

    public const string QuickToggleType = "Quick.ToggleType";
    public const string QuickCurrency = "Quick.Currency";
    public const string QuickRate = "Quick.Rate";
    public const string QuickAmount = "Quick.Amount";
    public const string Undo = "Undo";
    public const string Help = "Help";
    public const string PreviousPeriod = "Period.Previous";
    public const string NextPeriod = "Period.Next";
    public const string TxChangeCategory = "Tx.ChangeCategory";
    public const string TxDelete = "Tx.Delete";
    public const string GlobalQuickAdd = "Global.QuickAdd";
    public const string GoPrefix = "Go.";

    /// <summary>Screens in the order of the navigation list (the id is the page key).</summary>
    public static readonly string[] Pages = ["dashboard", "transactions", "budgets", "events", "recurring", "reports", "settings"];

    public static IReadOnlyList<ShortcutDef> Definitions { get; } =
    [
        new(QuickToggleType, "F2", "KeyF2"),
        new(QuickCurrency, "F3", "KeyF3"),
        new(QuickRate, "F4", "KeyF4"),
        new(QuickAmount, "Ctrl+L", "KeyCtrlL"),
        new(Undo, "Ctrl+Z", "KeyCtrlZ"),
        .. Pages.Select((p, i) => new ShortcutDef(GoPrefix + p, "Ctrl+" + (i + 1), "KeyGoTo", p)),
        new(PreviousPeriod, "Alt+Left", "KeyPrevPeriod"),
        new(NextPeriod, "Alt+Right", "KeyNextPeriod"),
        new(TxChangeCategory, "Ctrl+K", "KeyCtrlK"),
        new(TxDelete, "Delete", "KeyDel"),
        new(Help, "F1", "KeyF1"),
        new(GlobalQuickAdd, "Ctrl+Alt+B", "KeyGlobal"),
    ];

    /// <summary>Keys the program needs for itself or that belong to the system; they cannot be assigned.</summary>
    private static readonly string[] Reserved =
        ["Enter", "Ctrl+Enter", "Tab", "Shift+Tab", "Escape", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+A", "Ctrl+Y", "Alt+F4", "Space", "Back"];

    private SettingsStore? _store;

    /// <summary>Raised with the id of the command whose keys changed.</summary>
    public event Action<string>? Changed;

    public void Initialize(SettingsStore store) => _store = store;

    public ShortcutDef Definition(string id) => Definitions.First(d => d.Id == id);

    private string Stored(string id) =>
        _store?.Current.Shortcuts.TryGetValue(id, out var text) == true && TryParse(text, out _, out _) ? Normalize(text) : Definition(id).Default;

    public string KeysOf(string id) => Stored(id);

    /// <summary>Keys for display, e.g. "Ctrl + Alt + B".</summary>
    public string DisplayOf(string id) => Display(Stored(id));

    public bool IsCustom(string id) => !string.Equals(Stored(id), Definition(id).Default, StringComparison.OrdinalIgnoreCase);

    public (Key Key, ModifierKeys Modifiers) GestureOf(string id)
    {
        TryParse(Stored(id), out var key, out var modifiers);
        return (key, modifiers);
    }

    public bool Matches(string id, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var (k, m) = GestureOf(id);
        return key == k && Keyboard.Modifiers == m;
    }

    /// <summary>Gives a command new keys. Returns null on success, otherwise the reason (as text for the user).</summary>
    public string? Set(string id, Key key, ModifierKeys modifiers)
    {
        if (IsModifier(key)) return null;   // still waiting for the real key
        var error = Validate(id, key, modifiers);
        if (error is not null) return error;

        var text = Format(key, modifiers);
        _store?.Update(s =>
        {
            var copy = new Dictionary<string, string>(s.Shortcuts);
            if (string.Equals(text, Definition(id).Default, StringComparison.OrdinalIgnoreCase)) copy.Remove(id); else copy[id] = text;
            return s with { Shortcuts = copy };
        });
        Changed?.Invoke(id);
        return null;
    }

    public void Reset(string id)
    {
        _store?.Update(s =>
        {
            var copy = new Dictionary<string, string>(s.Shortcuts);
            copy.Remove(id);
            return s with { Shortcuts = copy };
        });
        Changed?.Invoke(id);
    }

    public string? Validate(string id, Key key, ModifierKeys modifiers)
    {
        var text = Format(key, modifiers);
        var allowedAlone = key is >= Key.F1 and <= Key.F24 || key is Key.Delete or Key.Insert;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && !allowedAlone)
            return Res.Get("ShortcutNeedsModifier");
        if (Reserved.Any(r => string.Equals(r, text, StringComparison.OrdinalIgnoreCase)))
            return Res.Get("ShortcutReserved");
        var other = Definitions.FirstOrDefault(d => d.Id != id && string.Equals(Stored(d.Id), text, StringComparison.OrdinalIgnoreCase));
        if (other is not null) return Res.Format("ShortcutUsedBy", Describe(other));
        return null;
    }

    public static string Describe(ShortcutDef def) => def.Argument is { } page
        ? Res.Format(def.DescriptionKey, Res.Get("Nav" + char.ToUpperInvariant(page[0]) + page[1..]))
        : Res.Get(def.DescriptionKey);

    // ----- text <-> keys -----

    public static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        Key.Return => "Enter",      // the enum prints its alias "Return"
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        _ => key.ToString(),
    };

    public static string Normalize(string text) => TryParse(text, out var k, out var m) ? Format(k, m) : text;

    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        var last = parts[^1];
        if (last.Length == 1 && char.IsDigit(last[0])) key = Key.D0 + (last[0] - '0');
        else if (!Enum.TryParse(last, ignoreCase: true, out key) || key == Key.None) return false;
        return !IsModifier(key);
    }

    public static string Display(string text) => text.Replace("+", " + ").Replace("Left", "←").Replace("Right", "→").Replace("Delete", "Del");
}
