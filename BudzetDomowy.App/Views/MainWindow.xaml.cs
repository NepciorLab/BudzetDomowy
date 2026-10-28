using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public ICommand GoCommand { get; }
    public ICommand ShowShortcutsCommand { get; }

    public MainWindow(MainViewModel vm, SettingsStore store)
    {
        _vm = vm;
        ShowShortcutsCommand = new RelayCommand(() =>
        {
            vm.Navigate("settings");
            vm.Settings.SelectedTab = SettingsViewModel.ShortcutsTab;
        });
        GoCommand = new RelayCommand<string>(key => { if (key is not null) vm.Navigate(key); });
        DataContext = vm;
        InitializeComponent();
        UiScaling.Attach(this);

        WindowPlacement.Apply(this, store.Current);
        Closing += (_, _) => store.Update(s => WindowPlacement.Capture(this, s));
        RebuildKeyBindings();
        HelpService.Install();
        HelpService.PageTopic = PageTopic;
        HelpService.PickingChanged += OnPickingChanged;
        Closed += (_, _) =>
        {
            HelpService.PickingChanged -= OnPickingChanged;
            HelpService.StopPicking();
        };
        KeyDown += OnWindowKeyDown;
        PreviewKeyDown += OnEntryKey;
        ShortcutMap.Instance.Changed += OnShortcutChanged;
        Closed += (_, _) => ShortcutMap.Instance.Changed -= OnShortcutChanged;
        Activated += async (_, _) => await vm.CheckRecurringOnActivationAsync();
        vm.SyncDetailsRequested += ShowSyncDetails;
    }

    private void OnShortcutChanged(string id) => RebuildKeyBindings();

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpService.TogglePicking();
        HelpButton.IsChecked = HelpService.IsPicking;
    }

    private void OnPickingChanged() => HelpButton.IsChecked = HelpService.IsPicking;

    /// <summary>The help topic of the open screen (the tab of Settings or Reports as well).</summary>
    private string? PageTopic() => _vm.SelectedNav?.Key switch
    {
        "settings" => "settings." + (_vm.Settings.SelectedTab switch
        {
            0 => "general", 1 => "appearance", 2 => "categories", 3 => "importexport", 4 => "diagnostics", 5 => "backup", 6 => "shortcuts", 7 => "places", _ => "general",
        }),
        "reports" when _vm.CurrentPage is ReportsViewModel reports => "reports." + (reports.TabIndex switch { 1 => "compare", 2 => "places", 3 => "charts", _ => "overview" }),
        { } key => key,
        _ => null,
    };

    /// <summary>W and Q (or the keys chosen in Settings) open the window for a new expense or income, but never while text is being typed.</summary>
    private void OnEntryKey(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var expense = ShortcutMap.Instance.Matches(ShortcutMap.NewExpense, e);
        var income = !expense && ShortcutMap.Instance.Matches(ShortcutMap.NewIncome, e);
        if (!expense && !income) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or DatePicker or System.Windows.Documents.TextElement) return;   // typing (or choosing from a list)

        e.Handled = true;
        if (expense) _vm.NewExpenseCommand.Execute(null); else _vm.NewIncomeCommand.Execute(null);
    }

    /// <summary>
    /// Esc that nothing else wanted (the table first deselects, then clears its filters; the quick-add bar clears its fields;
    /// open lists close themselves) takes the user back to the dashboard.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBoxBase { } box && box is not TextBox { Text.Length: 0 }) return;   // typing: Esc belongs to the text
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
        if (_vm.SelectedNav?.Key == "dashboard") return;

        _vm.Navigate("dashboard");
        e.Handled = true;
    }

    /// <summary>The window's keys, as set in Settings (defaults when nothing was changed).</summary>
    private void RebuildKeyBindings()
    {
        InputBindings.Clear();
        var map = ShortcutMap.Instance;

        void Add(string id, ICommand command, object? parameter = null)
        {
            var (key, modifiers) = map.GestureOf(id);
            InputBindings.Add(new KeyBinding(command, key, modifiers) { CommandParameter = parameter });
        }

        Add(ShortcutMap.ShortcutsList, ShowShortcutsCommand);   // F1, the help, is handled for the whole program by HelpService
        Add(ShortcutMap.Undo, _vm.UndoCommand);
        foreach (var page in ShortcutMap.Pages) Add(ShortcutMap.GoPrefix + page, GoCommand, page);
        Add(ShortcutMap.PreviousPeriod, _vm.PreviousPeriodCommand);
        Add(ShortcutMap.NextPeriod, _vm.NextPeriodCommand);
    }

    private void ShowSyncDetails()
    {
        var window = new SyncDetailsWindow(new SyncDetailsViewModel(_vm.Core)) { Owner = this };
        window.ShowDialog();
    }
}
