using System.Diagnostics;
using System.Windows;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Closes the program and starts it again (after a folder was changed).</summary>
public interface IAppRestarter
{
    void Restart();
}

public sealed class AppRestarter : IAppRestarter
{
    public const string RestartedFlag = "--restarted";

    public void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (AppPaths.Profile is { } profile)
                {
                    info.ArgumentList.Add("--profile");
                    info.ArgumentList.Add(profile);
                }
                info.ArgumentList.Add(RestartedFlag);   // the new copy waits until this one has let go of the instance lock
                Process.Start(info);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Restart failed.", e);
        }
        Application.Current.Shutdown();
    }
}
