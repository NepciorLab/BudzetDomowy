using BudzetDomowy.App.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BudzetDomowy.App.ViewModels;

namespace BudzetDomowy.App.Views;

/// <summary>The quick entry bar alone in a small window: opened by the global shortcut or the tray icon, closes after saving or on Esc.</summary>
public partial class QuickAddWindow : Window
{
    private readonly QuickAddViewModel _vm;

    public QuickAddWindow(QuickAddViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        UiScaling.Attach(this);
        Bar.DataContext = vm;
        vm.Saved += OnSaved;
        Closed += (_, _) => vm.Saved -= OnSaved;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + 80;
            Activate();
            Bar.FocusAmount();
        };
    }

    private void OnSaved()
    {
        // the bar clears itself and refocuses the amount; in this window a saved entry means we are done
        Dispatcher.BeginInvoke(Close);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Esc first closes an open drop-down list
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
        if (Keyboard.FocusedElement is DependencyObject d && IsInOpenCombo(d)) return;
        e.Handled = true;
        Close();
    }

    private static bool IsInOpenCombo(DependencyObject d)
    {
        for (; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is ComboBox { IsDropDownOpen: true }) return true;
        return false;
    }
}
