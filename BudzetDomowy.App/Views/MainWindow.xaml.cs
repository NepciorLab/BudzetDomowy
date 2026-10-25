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

    public ICommand FocusAmountCommand { get; }
    public ICommand GoCommand { get; }
    public ICommand FocusCurrencyCommand { get; }
    public ICommand FocusRateCommand { get; }
    public ICommand ShowShortcutsCommand { get; }

    public MainWindow(MainViewModel vm, SettingsStore store)
    {
        _vm = vm;
        FocusAmountCommand = new RelayCommand(() => QuickBar.FocusAmount());
        FocusCurrencyCommand = new RelayCommand(() => QuickBar.FocusCurrency());
        FocusRateCommand = new RelayCommand(() => QuickBar.FocusRate());
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
        KeyDown += OnWindowKeyDown;
        ShortcutMap.Instance.Changed += OnShortcutChanged;
        Closed += (_, _) => ShortcutMap.Instance.Changed -= OnShortcutChanged;
        vm.SyncDetailsRequested += ShowSyncDetails;
        Loaded += (_, _) => QuickBar.FocusAmount();
    }

    private void OnShortcutChanged(string id) => RebuildKeyBindings();

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

        Add(ShortcutMap.QuickToggleType, _vm.QuickAdd.ToggleTypeCommand);
        Add(ShortcutMap.Help, ShowShortcutsCommand);
        Add(ShortcutMap.Undo, _vm.UndoCommand);
        Add(ShortcutMap.QuickCurrency, FocusCurrencyCommand);
        Add(ShortcutMap.QuickRate, FocusRateCommand);
        Add(ShortcutMap.QuickAmount, FocusAmountCommand);
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
