using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BudzetDomowy.App.Infrastructure.Updates;

/// <summary>Reads the newest release of a GitHub repository and downloads its program file.</summary>
public sealed class GitHubReleases
{
    private readonly HttpClient _http;
    private readonly Func<string?> _token;

    public GitHubReleases(HttpClient http, Func<string?> token)
    {
        _http = http;
        _token = token;
    }

    public static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    private HttpRequestMessage Request(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BudzetDomowy", "1"));
        request.Headers.Accept.ParseAdd(accept);
        if (_token() is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<UpdateCheck> CheckAsync(string repository, Version current, CancellationToken ct = default)
    {
        try
        {
            using var request = Request($"https://api.github.com/repos/{repository}/releases/latest", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new UpdateCheck(UpdateState.NotFound, null, Res.Get("UpdateNotFound"));
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(UpdateState.Failed, null, Res.Format("UpdateFailed", (int)response.StatusCode));

            var release = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (release is null) return new UpdateCheck(UpdateState.Failed, null, Res.Format("UpdateFailed", "?"));
            return release.Version > current
                ? new UpdateCheck(UpdateState.Available, release, Res.Format("UpdateAvailable", release.Version.ToString(3)))
                : new UpdateCheck(UpdateState.UpToDate, release, Res.Get("UpdateUpToDate"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheck(UpdateState.Failed, null, Res.Format("UpdateFailed", e.Message));
        }
    }

    /// <summary>The release in GitHub's JSON, or null when it has no usable version tag.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        ReleaseAsset? asset = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var list = assets.EnumerateArray().Select(a => new ReleaseAsset(
                a.GetProperty("name").GetString() ?? "",
                a.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "",
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0)).ToList();
            // the program file itself is best; the release archive (with the exe inside) is the fallback
            asset = list.FirstOrDefault(a => a.Name.Equals("BudzetDomowy.exe", StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(a => a.Name.StartsWith("BudzetDomowy", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
        return new ReleaseInfo(version, tag, notes, page, asset);
    }

    public async Task DownloadAsync(ReleaseAsset asset, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // with a token the asset has to be fetched through the API address; the public address needs none
        var useApi = _token() is { Length: > 0 } && asset.ApiUrl.Length > 0;
        using var request = Request(useApi ? asset.ApiUrl : asset.DownloadUrl, useApi ? "application/octet-stream" : "*/*");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }
        if (asset.Size > 0 && done != asset.Size) throw new IOException(Res.Get("UpdateIncomplete"));
    }
}
