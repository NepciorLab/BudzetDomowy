using System.Net.Http;
using System.Reflection;

namespace BudzetDomowy.App.Infrastructure.Updates;

/// <summary>Asks the user whether to install a newer version that was found.</summary>
public interface IUpdatePrompt
{
    bool Confirm(ReleaseInfo release, Version current);
}

/// <summary>Checks GitHub for a newer version at startup (when switched on) or on request, and installs it.</summary>
public sealed class UpdateService
{
    private readonly SettingsStore _store;
    private readonly GitHubReleases _releases;
    private readonly IUpdatePrompt _prompt;
    private readonly IAppRestarter _restarter;
    private readonly string? _exePath;
    private readonly Version? _currentVersion;

    public UpdateService(SettingsStore store, GitHubReleases releases, IUpdatePrompt prompt, IAppRestarter restarter, string? exePath = null, Version? currentVersion = null)
    {
        _store = store;
        _releases = releases;
        _prompt = prompt;
        _restarter = restarter;
        _exePath = exePath ?? Environment.ProcessPath;
        _currentVersion = currentVersion;
    }

    public static Version CurrentVersion =>
        Version.TryParse((Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0], out var v) ? v : new Version(0, 0, 0);

    /// <summary>The running version (tests give another one).</summary>
    public Version Current => _currentVersion ?? CurrentVersion;

    public Task<UpdateCheck> CheckAsync(CancellationToken ct = default) =>
        _releases.CheckAsync(_store.Current.UpdateRepository, Current, ct);

    /// <summary>Whether this program can replace itself (a single exe file in a folder it may write to).</summary>
    public bool CanInstall => _exePath is { } exe && File.Exists(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                              && !Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the release and puts it in place of the running exe. The caller restarts the program afterwards.</summary>
    public async Task<string?> InstallAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.Asset is not { } asset) return Res.Get("UpdateNoAsset");
        if (!CanInstall || _exePath is null) return Res.Get("UpdateCannotInstall");

        var work = Path.Combine(Path.GetTempPath(), "budzet-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var downloaded = Path.Combine(work, asset.Name);
            await _releases.DownloadAsync(asset, downloaded, progress, ct).ConfigureAwait(false);
            var exe = UpdateInstaller.Prepare(downloaded, Path.Combine(work, "unpacked"), Current);
            UpdateInstaller.Replace(exe, _exePath);
            AppLog.Info($"Updated to {release.Version} from {release.PageUrl}.");
            return null;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            AppLog.Error("Installing the update failed.", e);
            return Res.Format("UpdateInstallFailed", e.Message);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Restart() => _restarter.Restart();

    /// <summary>
    /// The startup check: nothing happens when automatic updates are off, no newer version exists or anything goes wrong
    /// (no internet is not worth a message). A newer version is installed only after the user agreed.
    /// </summary>
    public async Task RunAutomaticAsync(CancellationToken ct = default)
    {
        if (!_store.Current.AutoUpdate) return;
        var check = await CheckAsync(ct).ConfigureAwait(true);
        if (check is not { State: UpdateState.Available, Release: { } release }) return;
        if (!_prompt.Confirm(release, Current)) return;

        var error = await InstallAsync(release, null, ct).ConfigureAwait(true);
        if (error is null) Restart();
        else AppLog.Warn("Automatic update failed: " + error);
    }
}
