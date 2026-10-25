using System.Windows;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Questions to the user that need a yes or a no (tests answer them without a window).</summary>
public interface IMessageDialogs
{
    bool Confirm(string title, string text);
}

public sealed class WpfMessageDialogs : IMessageDialogs
{
    public bool Confirm(string title, string text) =>
        MessageBox.Show(Application.Current?.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
