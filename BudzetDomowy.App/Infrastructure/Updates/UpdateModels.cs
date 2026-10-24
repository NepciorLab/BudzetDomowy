namespace BudzetDomowy.App.Infrastructure.Updates;

public sealed record ReleaseAsset(string Name, string ApiUrl, string DownloadUrl, long Size);

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, ReleaseAsset? Asset);

public enum UpdateState
{
    /// <summary>This is the newest version.</summary>
    UpToDate,

    /// <summary>A newer version exists.</summary>
    Available,

    /// <summary>The repository has no release, or it is private and no (valid) token was given.</summary>
    NotFound,

    /// <summary>No connection or an unexpected answer.</summary>
    Failed,
}

public sealed record UpdateCheck(UpdateState State, ReleaseInfo? Release, string Message);
