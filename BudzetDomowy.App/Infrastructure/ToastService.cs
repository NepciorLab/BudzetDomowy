using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Discreet message with an optional Undo button, used instead of confirmation dialogs.</summary>
public sealed partial class ToastService : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Action? _undo;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _canUndo;

    public ToastService() => _timer.Tick += (_, _) => Hide();

    public void Show(string message, Action? undo = null)
    {
        _undo = undo;
        Message = message;
        CanUndo = undo is not null;
        IsVisible = true;
        _timer.Stop();
        _timer.Start();
    }

    [RelayCommand]
    public void Undo()
    {
        var undo = _undo;
        Hide();
        undo?.Invoke();
    }

    [RelayCommand]
    public void Hide()
    {
        _timer.Stop();
        _undo = null;
        IsVisible = false;
        CanUndo = false;
    }

    /// <summary>Ctrl+Z: undoes the last action while its toast is still visible.</summary>
    public bool TryUndoLast()
    {
        if (!IsVisible || _undo is null) return false;
        Undo();
        return true;
    }
}
