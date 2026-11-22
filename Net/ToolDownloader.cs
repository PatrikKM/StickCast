using System.IO.Compression;

namespace StickCast.Net;

/// <summary>
/// Locates and downloads external CLI tools (yt-dlp.exe, ffmpeg.exe) into a writable directory.
/// </summary>
public static class ToolDownloader
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string GetToolsDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        if (IsDirectoryWritable(baseDir))
            return baseDir;

        var localTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StickCast",
            "tools");
        Directory.CreateDirectory(localTools);
        return localTools;
    }

    internal static string? FindTool(string exeName)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
            candidates.Add(Path.Combine(AppContext.BaseDirectory, exeName));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            candidates.Add(Path.Combine(localAppData, "StickCast", "tools", exeName));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            candidates.Add(Path.Combine(appData, "StickCast", "tools", exeName));

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { }
        }

        foreach (var rawDir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var p = Path.Combine(dir, exeName);
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        return null;
    }

    public static async Task<string> DownloadYtDlpAsync(
        IProgress<(double? Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetToolsDirectory();
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "yt-dlp.exe");
        var tmp = target + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";

        try
        {
            progress?.Report((0, "Stahuji yt-dlp.exe…"));
            await DownloadFileAsync(YtDlpUrl, tmp, "Stahuji yt-dlp.exe", progress, ct);
            File.Move(tmp, target, overwrite: true);
            progress?.Report((100, "yt-dlp.exe staženo."));
            return target;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public static async Task<string> DownloadFfmpegAsync(
        IProgress<(double? Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetToolsDirectory();
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "ffmpeg.exe");
        var zipTmp = Path.Combine(Path.GetTempPath(), $"stickcast_ffmpeg_{Guid.NewGuid():N}.zip");
        var exeTmp = target + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";

        try
        {
            progress?.Report((0, "Stahuji ffmpeg (ZIP archiv)…"));
            await DownloadFileAsync(FfmpegZipUrl, zipTmp, "Stahuji ffmpeg", progress, ct);

            progress?.Report((null, "Rozbaluji ffmpeg.exe…"));
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipTmp);
                var entry = archive.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.FullName).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Stažený ZIP archiv neobsahuje ffmpeg.exe.");

                ct.ThrowIfCancellationRequested();
                entry.ExtractToFile(exeTmp, overwrite: true);
            }, ct);

            File.Move(exeTmp, target, overwrite: true);
            progress?.Report((100, "ffmpeg.exe staženo."));
            return target;
        }
        finally
        {
            TryDelete(zipTmp);
            TryDelete(exeTmp);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string label,
        IProgress<(double? Percent, string Status)>? progress,
        CancellationToken ct)
    {
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dlCts.CancelAfter(TimeSpan.FromMinutes(15));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("StickCast/1.0");

        using var resp = await HttpClients.LongTransfer.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, dlCts.Token);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(dlCts.Token);
        await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        var buf = new byte[65536];
        long readTotal = 0;
        long lastReportBytes = 0;

        while (true)
        {
            int n = await src.ReadAsync(buf, dlCts.Token);
            if (n <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), dlCts.Token);
            readTotal += n;

            if (readTotal - lastReportBytes >= 262144) // ~256 KB
            {
                lastReportBytes = readTotal;
                double? pct = total > 0 ? Math.Clamp(readTotal * 100.0 / total.Value, 0, 100) : null;
                var mb = readTotal / (1024.0 * 1024.0);
                var status = total > 0
                    ? $"{label} ({mb:F1} / {total.Value / (1024.0 * 1024.0):F1} MB)"
                    : $"{label} ({mb:F1} MB)";
                progress?.Report((pct, status));
            }
        }
        await dst.FlushAsync(dlCts.Token);
    }

    private static bool IsDirectoryWritable(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
