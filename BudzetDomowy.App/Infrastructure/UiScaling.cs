using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Makes a window follow the interface scale chosen in Settings (small, medium, large).</summary>
public static class UiScaling
{
    public static void Attach(Window window)
    {
        void Apply()
        {
            if (window.Content is not FrameworkElement root) return;
            var factor = UiPreferences.Instance.ScaleFactor;
            root.LayoutTransform = Math.Abs(factor - 1) < 0.001 ? Transform.Identity : new ScaleTransform(factor, factor);
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UiPreferences.ScaleFactor)) Apply();
        }

        window.Loaded += (_, _) => Apply();
        window.SourceInitialized += (_, _) => Apply();
        UiPreferences.Instance.PropertyChanged += OnChanged;
        window.Closed += (_, _) => UiPreferences.Instance.PropertyChanged -= OnChanged;
        Apply();
    }
}
