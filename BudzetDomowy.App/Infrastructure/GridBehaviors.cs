using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>
/// Table behaviours that need no code in the views:
/// <c>AutoFit</c> sizes columns to their content, <c>ColumnChooserKey</c> lets the user hide columns from the header's right-click menu
/// (the choice is remembered under that key).
/// </summary>
public static class GridBehaviors
{
    // ----- auto width -----

    public static readonly DependencyProperty AutoFitProperty = DependencyProperty.RegisterAttached(
        "AutoFit", typeof(bool), typeof(GridBehaviors), new PropertyMetadata(false, OnAutoFitChanged));

    public static bool GetAutoFit(DependencyObject d) => (bool)d.GetValue(AutoFitProperty);
    public static void SetAutoFit(DependencyObject d, bool value) => d.SetValue(AutoFitProperty, value);

    private static readonly ConditionalWeakTable<DataGridColumn, object> OriginalWidths = new();
    private static readonly ConditionalWeakTable<DataGrid, object> Hooked = new();

    private static void OnAutoFitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        Hook(grid);
        Refit(grid);
    }

    private static void Hook(DataGrid grid)
    {
        if (Hooked.TryGetValue(grid, out _)) return;
        Hooked.Add(grid, new object());

        grid.Loaded += (_, _) => Refit(grid);
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            .AddValueChanged(grid, (_, _) =>
            {
                if (grid.ItemsSource is INotifyCollectionChanged incc) incc.CollectionChanged += (_, _) => Schedule(grid);
                Schedule(grid);
            });
        if (grid.ItemsSource is INotifyCollectionChanged existing) existing.CollectionChanged += (_, _) => Schedule(grid);
    }

    private sealed class Slot { public DispatcherOperation? Operation; }

    private static readonly ConditionalWeakTable<DataGrid, Slot> Pending = new();

    /// <summary>Coalesces bursts of changes into one re-fit.</summary>
    private static void Schedule(DataGrid grid)
    {
        if (!GetAutoFit(grid)) return;
        var slot = Pending.GetOrCreateValue(grid);
        if (slot.Operation is { Status: DispatcherOperationStatus.Pending }) return;
        slot.Operation = grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => Refit(grid));
    }

    /// <summary>Applies the current setting: content width when on, the designed widths when off.</summary>
    public static void Refit(DataGrid grid)
    {
        var on = GetAutoFit(grid);
        foreach (var column in grid.Columns)
        {
            if (!OriginalWidths.TryGetValue(column, out _)) OriginalWidths.Add(column, column.Width);
            if (on)
            {
                // setting it to something else first makes WPF measure the content again
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Pixel);
                column.Width = DataGridLength.Auto;
            }
            else if (OriginalWidths.TryGetValue(column, out var original))
            {
                column.Width = (DataGridLength)original;
            }
        }
        grid.UpdateLayout();
    }

    // ----- visible columns -----

    public static readonly DependencyProperty ColumnChooserKeyProperty = DependencyProperty.RegisterAttached(
        "ColumnChooserKey", typeof(string), typeof(GridBehaviors), new PropertyMetadata(null, OnChooserKeyChanged));

    public static string? GetColumnChooserKey(DependencyObject d) => (string?)d.GetValue(ColumnChooserKeyProperty);
    public static void SetColumnChooserKey(DependencyObject d, string? value) => d.SetValue(ColumnChooserKeyProperty, value);

    private static void OnChooserKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not string) return;
        grid.Loaded += (_, _) => ApplyHidden(grid);
        grid.PreviewMouseRightButtonDown += OnRightClick;
    }

    private static void OnRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || GetColumnChooserKey(grid) is not { } key) return;
        if (FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header) return;

        var menu = new ContextMenu { PlacementTarget = header };
        menu.Items.Add(new MenuItem { Header = Res.Get("ColumnsMenuTitle"), IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var item = new MenuItem
            {
                Header = column.Header?.ToString() is { Length: > 0 } h ? h : Res.Format("ColumnsUnnamed", i + 1),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                // never hide the last visible column: the table would vanish with no way to bring it back
                if (grid.Columns.All(c => c.Visibility != Visibility.Visible))
                {
                    column.Visibility = Visibility.Visible;
                    item.IsChecked = true;
                }
                SaveHidden(grid, key);
                if (GetAutoFit(grid)) Refit(grid);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = Res.Get("ColumnsReset") };
        reset.Click += (_, _) =>
        {
            foreach (var c in grid.Columns) c.Visibility = Visibility.Visible;
            SaveHidden(grid, key);
            if (GetAutoFit(grid)) Refit(grid);
        };
        menu.Items.Add(reset);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void SaveHidden(DataGrid grid, string key) =>
        UiPreferences.Instance.SetHiddenColumns(key, grid.Columns.Select((c, i) => (c, i)).Where(x => x.c.Visibility != Visibility.Visible).Select(x => x.i));

    public static void ApplyHidden(DataGrid grid)
    {
        if (GetColumnChooserKey(grid) is not { } key) return;
        var hidden = UiPreferences.Instance.HiddenColumns(key);
        for (var i = 0; i < grid.Columns.Count; i++)
            grid.Columns[i].Visibility = hidden.Contains(i) ? Visibility.Collapsed : Visibility.Visible;
        if (grid.Columns.All(c => c.Visibility != Visibility.Visible))
            foreach (var c in grid.Columns) c.Visibility = Visibility.Visible;
        if (GetAutoFit(grid)) Refit(grid);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
