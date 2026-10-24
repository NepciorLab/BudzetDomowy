using System.Net.Http;
using System.Diagnostics;
using System.IO.Compression;

namespace BudzetDomowy.App.Infrastructure.Updates;

/// <summary>Puts a downloaded program file in place of the running one (Windows lets a running exe be renamed, not overwritten).</summary>
public static class UpdateInstaller
{
    public const string ExeName = "BudzetDomowy.exe";

    /// <summary>The program file from a download: the file itself or the exe inside a release archive. Checks that it really is a newer program.</summary>
    public static string Prepare(string downloaded, string workDirectory, Version current)
    {
        Directory.CreateDirectory(workDirectory);
        var exe = downloaded;
        if (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            exe = Path.Combine(workDirectory, ExeName);
            using var zip = ZipFile.OpenRead(downloaded);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException(Res.Get("UpdateNoExeInArchive"));
            entry.ExtractToFile(exe, overwrite: true);
        }

        using (var stream = File.OpenRead(exe))
        {
            var header = new byte[2];
            if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidDataException(Res.Get("UpdateNotAProgram"));
        }

        var info = FileVersionInfo.GetVersionInfo(exe);
        if (!Version.TryParse(info.FileVersion, out var version) || Normalize(version) <= Normalize(current))
            throw new InvalidDataException(Res.Get("UpdateNotNewer"));
        return exe;
    }

    // 0.2.0 and 0.2.0.0 are the same version
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Renames the running exe to *.old and puts the new file in its place; the old one comes back if anything fails.</summary>
    public static void Replace(string newExe, string targetExe)
    {
        var old = targetExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(targetExe, old);
        try
        {
            File.Copy(newExe, targetExe);
        }
        catch
        {
            if (File.Exists(targetExe)) File.Delete(targetExe);
            File.Move(old, targetExe);
            throw;
        }
    }

    /// <summary>Deletes the previous version left behind by the last update (call at startup).</summary>
    public static void CleanUp(string targetExe)
    {
        try
        {
            var old = targetExe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // still in use, e.g. by the copy that is closing: next time
        }
    }
}
