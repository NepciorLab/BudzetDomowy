using System.Windows;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Views;

public partial class RecurringPromptWindow : Window
{
    public RecurringAnswer Answer { get; private set; } = RecurringAnswer.Later;

    public RecurringPromptWindow(RecurringQuestion question)
    {
        DataContext = question;
        InitializeComponent();
        UiScaling.Attach(this);
    }

    private void Yes_Click(object sender, RoutedEventArgs e) { Answer = RecurringAnswer.Yes; DialogResult = true; }

    private void No_Click(object sender, RoutedEventArgs e) { Answer = RecurringAnswer.No; DialogResult = true; }

    private void Later_Click(object sender, RoutedEventArgs e) { Answer = RecurringAnswer.Later; DialogResult = false; }
}

/// <summary>Asks in a small window above the main window; only while that window is on screen.</summary>
public sealed class WpfRecurringPrompt : IRecurringPrompt
{
    public bool CanAsk => Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

    public RecurringAnswer Ask(RecurringQuestion question)
    {
        var window = new RecurringPromptWindow(question) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return window.Answer;
    }
}
