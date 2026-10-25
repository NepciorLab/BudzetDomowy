using System.Windows;
using BudzetDomowy.App.Infrastructure;
using BudzetDomowy.App.Infrastructure.Updates;

namespace BudzetDomowy.App.Views;

public sealed class WpfUpdatePrompt : IUpdatePrompt
{
    public bool Confirm(ReleaseInfo release, Version current, bool dataIsNewer = false)
    {
        var text = Res.Format(dataIsNewer ? "UpdatePromptDataNewer" : "UpdatePromptText", release.Version.ToString(3), current.ToString(3));
        if (release.Notes.Length > 0) text += "\n\n" + (release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes);
        return MessageBox.Show(Application.Current?.MainWindow, text, Res.Get("UpdatePromptTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
    }
}
