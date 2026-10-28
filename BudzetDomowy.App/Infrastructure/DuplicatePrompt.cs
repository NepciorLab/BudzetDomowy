using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls.Primitives;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>What the user chose when a new entry already has a twin.</summary>
public enum DuplicateChoice { Add, Cancel, Merge }

/// <summary>An entry that is already there and looks like the new one.</summary>
public sealed record DuplicateInfo(DateOnly Date, string Place, string Category, decimal Amount, string Currency);

/// <summary>The question "such an entry is already there": tests answer it without a window.</summary>
public interface IDuplicatePrompt
{
    DuplicateChoice Ask(IReadOnlyList<DuplicateInfo> existing);
}

public sealed class WpfDuplicatePrompt : IDuplicatePrompt
{
    public DuplicateChoice Ask(IReadOnlyList<DuplicateInfo> existing)
    {
        var choice = DuplicateChoice.Cancel;
        // the window the user is working in (looked for before the new one exists: it must not become its own owner)
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);
        var window = new Window
        {
            Title = Res.Get("DupTitle"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MaxWidth = 620,
            Owner = owner,
        };
        window.WindowStartupLocation = window.Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowHelper.SetUseModernWindowStyle(window, true);

        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20), MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = Res.Get(existing.Count == 1 ? "DupIntroOne" : "DupIntroMany"), TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        foreach (var e in existing)
        {
            var money = e.Amount.ToString("N2", CultureInfo.CurrentCulture) + " " + e.Currency;
            var where = string.IsNullOrWhiteSpace(e.Place) ? "" : " · " + e.Place;
            panel.Children.Add(new TextBlock
            {
                Text = $"{e.Date.ToString("d", CultureInfo.CurrentCulture)}{where} · {e.Category} · {money}",
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 0, 0),
            });
        }
        panel.Children.Add(new TextBlock { Text = Res.Get("DupQuestion"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) });
        panel.Children.Add(new TextBlock { Text = Res.Get("DupMergeHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });

        Button Make(string key, DuplicateChoice result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = Res.Get(key), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) =>
            {
                choice = result;
                window.DialogResult = true;
            };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(Make("DupAdd", DuplicateChoice.Add));
        buttons.Children.Add(Make("DupCancel", DuplicateChoice.Cancel, isDefault: true, isCancel: true));      // the safe answer: Enter and Esc
        buttons.Children.Add(Make("DupMerge", DuplicateChoice.Merge));
        panel.Children.Add(buttons);

        window.Content = panel;
        UiScaling.Attach(window);
        window.ShowDialog();
        return choice;
    }
}
