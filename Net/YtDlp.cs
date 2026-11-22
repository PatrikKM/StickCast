using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StickCast.Net;

public sealed record YtSearchResult(string Title, string Url, string Duration, string Channel);

public sealed record YtResolvedMedia(
    string MediaUrl,
    string? Title = null,
    string? SubtitleSrtPath = null,
    string? SubtitleSource = null);

/// <summary>
/// Optional helper: if yt-dlp.exe is available (next to StickCast.exe or in PATH),
/// web pages like YouTube are converted to a direct media URL the dongle can play.
/// </summary>
internal static class YtDlp
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(25);
    private const string FormatSelector =
        "best[protocol=https][ext=mp4][vcodec^=avc1][acodec!=none]/" +
        "best[protocol=http][ext=mp4][vcodec^=avc1][acodec!=none]/" +
        "18/best[ext=mp4][vcodec^=avc1][acodec!=none]/best[ext=mp4][acodec!=none]/best[acodec!=none]/best";

    public static string? Find() => ToolDownloader.FindTool("yt-dlp.exe");

    public static bool IsVideoPortal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var h = uri.Host.ToLowerInvariant();
        string[] portals = { "youtube.", "youtu.be", "vimeo.", "dailymotion.", "twitch.", "tiktok.",
            "facebook.", "instagram.", "x.com", "twitter.", "ceskatelevize.", "stream.cz", "novaplus.", "iprima." };
        return portals.Any(h.Contains);
    }

    public static async Task<string> ResolveAsync(string exe, string url, CancellationToken ct = default)
    {
        var resolved = await ResolveWithSubtitlesAsync(exe, url, autoCzechSubtitles: false, ct);
        return resolved.MediaUrl;
    }

    public static async Task<YtResolvedMedia> ResolveWithSubtitlesAsync(
        string exe,
        string url,
        bool autoCzechSubtitles,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exe))
            throw new ArgumentException("Cesta k yt-dlp.exe je prázdná.", nameof(exe));
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL adresa je prázdná.", nameof(url));

        // Build candidate URLs: if music.youtube.com/watch?v=ID fails or lacks direct MP4 streams,
        // fallback to www.youtube.com/watch?v=ID which exposes the full progressive format list.
        var candidateUrls = new List<string> { url.Trim() };
        if (url.Contains("music.youtube.com/watch", StringComparison.OrdinalIgnoreCase))
        {
            var stdUrl = url.Replace("music.youtube.com/", "www.youtube.com/", StringComparison.OrdinalIgnoreCase);
            if (!candidateUrls.Contains(stdUrl, StringComparer.OrdinalIgnoreCase))
                candidateUrls.Add(stdUrl);
        }

        string? lastError = null;
        foreach (var candidateUrl in candidateUrls)
        {
            foreach (var fmt in new string?[] { FormatSelector, "best[acodec!=none]/best", null })
            {
                var (exitCode, stdout, stderr) = await RunDumpSingleJsonAsync(exe, candidateUrl, fmt, ct);
                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    var err = stderr.Trim();
                    lastError = err.Length > 0 ? err : $"ukončeno s kódem {exitCode}";
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var root = doc.RootElement;
                    string? mediaUrl = ExtractDirectMediaUrl(root);

                    if (string.IsNullOrWhiteSpace(mediaUrl) &&
                        TryExtractFirstEntryWatchUrl(root, out var firstWatchUrl) &&
                        !string.Equals(firstWatchUrl, candidateUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return await ResolveWithSubtitlesAsync(exe, firstWatchUrl!, autoCzechSubtitles, ct);
                    }

                    if (string.IsNullOrWhiteSpace(mediaUrl))
                    {
                        lastError = "yt-dlp nevrátil přímou URL streamu.";
                        continue;
                    }

                    var title = GetJsonString(root, "title");
                    string? subPath = null;
                    string? subSource = null;

                    if (autoCzechSubtitles)
                        (subPath, subSource) = await TryFetchCzechSubtitlesAsync(root, candidateUrl, ct);

                    return new YtResolvedMedia(mediaUrl, title, subPath, subSource);
                }
                catch (JsonException ex)
                {
                    lastError = "Neplatná JSON odpověď z yt-dlp: " + ex.Message;
                }
            }
        }

        throw new InvalidOperationException("yt-dlp nedokázal získat odkaz: " + (lastError ?? "neznámá chyba"));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDumpSingleJsonAsync(
        string exe,
        string targetUrl,
        string? formatSelector,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--dump-single-json");
        psi.ArgumentList.Add("--no-warnings");
        if (!string.IsNullOrWhiteSpace(formatSelector))
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatSelector);
        }
        psi.ArgumentList.Add(targetUrl);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit yt-dlp.exe.");

        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(p);
            await ObserveStreamsAsync(outTask, errTask);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new TimeoutException($"yt-dlp neodpověděl v časovém limitu ({(int)DefaultTimeout.TotalSeconds} s).");
        }

        return (p.ExitCode, await outTask, await errTask);
    }

    private static async Task<(string? Path, string? Source)> TryFetchCzechSubtitlesAsync(
        JsonElement root,
        string pageUrl,
        CancellationToken ct)
    {
        try
        {
            // 1. Native Czech subtitles (subtitles: cs, cs-CZ, cz)
            if (TryPickTrackUrl(root, "subtitles", new[] { "cs", "cs-CZ", "cs-cz", "cz" }, out var csUrl, out _))
            {
                var srt = await DownloadAndFormatSrtAsync(csUrl!, translateToCzech: false, ct);
                if (srt != null)
                    return (SaveCachedSrt(pageUrl, srt), "české titulky");
            }

            // 2. YouTube A.I. auto-generated Czech captions (Czech videos)
            if (TryPickTrackUrl(root, "automatic_captions", new[] { "cs", "cs-CZ", "cs-orig" }, out var autoCsUrl, out _))
            {
                var srt = await DownloadAndFormatSrtAsync(autoCsUrl!, translateToCzech: false, ct);
                if (srt != null)
                    return (SaveCachedSrt(pageUrl, srt), "A.I. české titulky");
            }

            // 3. Any available subtitle/auto-caption track -> try YouTube tlang=cs first, then translate fallback
            string[] fallbackLangs = { "en", "en-US", "en-GB", "en-orig", "sk", "de", "pl", "es", "fr" };
            string? srcUrl = null;
            string? srcLang = null;

            if (!TryPickTrackUrl(root, "subtitles", fallbackLangs, out srcUrl, out srcLang) &&
                !TryPickTrackUrl(root, "automatic_captions", fallbackLangs, out srcUrl, out srcLang) &&
                !TryPickAnyTrackUrl(root, "subtitles", out srcUrl, out srcLang))
            {
                TryPickAnyTrackUrl(root, "automatic_captions", out srcUrl, out srcLang);
            }

            if (string.IsNullOrEmpty(srcUrl))
                return (null, null);

            // Try YouTube's built-in tlang=cs if it's a timedtext URL
            if (srcUrl.Contains("youtube.com/api/timedtext", StringComparison.OrdinalIgnoreCase) &&
                !srcUrl.Contains("tlang=", StringComparison.OrdinalIgnoreCase))
            {
                var tlangUrl = srcUrl + "&tlang=cs";
                var ytTranslated = await DownloadAndFormatSrtAsync(tlangUrl, translateToCzech: false, ct);
                if (ytTranslated != null)
                    return (SaveCachedSrt(pageUrl, ytTranslated), "A.I. české titulky (YouTube)");
            }

            bool isSlovak = srcLang != null && srcLang.StartsWith("sk", StringComparison.OrdinalIgnoreCase);
            var fallbackSrt = await DownloadAndFormatSrtAsync(srcUrl, translateToCzech: !isSlovak, ct);
            if (fallbackSrt != null)
            {
                var label = isSlovak ? "slovenské titulky" : $"A.I. přeložené české titulky ({srcLang ?? "auto"})";
                return (SaveCachedSrt(pageUrl, fallbackSrt), label);
            }
        }
        catch { /* subtitle failure must never block video playback */ }

        return (null, null);
    }

    private static bool TryPickTrackUrl(
        JsonElement root,
        string containerName,
        string[] preferredLangs,
        out string? trackUrl,
        out string? matchedLang)
    {
        trackUrl = null;
        matchedLang = null;
        if (!root.TryGetProperty(containerName, out var container) || container.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var lang in preferredLangs)
        {
            if (container.TryGetProperty(lang, out var arr) && TryExtractBestTrackUrl(arr, out trackUrl))
            {
                matchedLang = lang;
                return true;
            }
        }

        foreach (var prop in container.EnumerateObject())
        {
            foreach (var lang in preferredLangs)
            {
                if (prop.Name.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase) &&
                    TryExtractBestTrackUrl(prop.Value, out trackUrl))
                {
                    matchedLang = prop.Name;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryPickAnyTrackUrl(
        JsonElement root,
        string containerName,
        out string? trackUrl,
        out string? matchedLang)
    {
        trackUrl = null;
        matchedLang = null;
        if (!root.TryGetProperty(containerName, out var container) || container.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in container.EnumerateObject())
        {
            if (prop.Name.Equals("live_chat", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryExtractBestTrackUrl(prop.Value, out trackUrl))
            {
                matchedLang = prop.Name;
                return true;
            }
        }
        return false;
    }

    private static bool TryExtractBestTrackUrl(JsonElement arr, out string? url)
    {
        url = null;
        if (arr.ValueKind != JsonValueKind.Array) return false;

        string? vtt = null, srt = null, any = null;
        foreach (var item in arr.EnumerateArray())
        {
            var ext = GetJsonString(item, "ext") ?? "";
            var u = GetJsonString(item, "url");
            if (string.IsNullOrWhiteSpace(u)) continue;

            if (ext.Equals("srt", StringComparison.OrdinalIgnoreCase) && srt == null) srt = u;
            else if (ext.Equals("vtt", StringComparison.OrdinalIgnoreCase) && vtt == null) vtt = u;
            else if (any == null && !ext.Equals("json3", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals("srv1", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals("srv2", StringComparison.OrdinalIgnoreCase) &&
                     !ext.Equals("srv3", StringComparison.OrdinalIgnoreCase))
            {
                any = u;
            }
        }

        url = srt ?? vtt ?? any;
        return url != null;
    }

    private static async Task<string?> DownloadAndFormatSrtAsync(string trackUrl, bool translateToCzech, CancellationToken ct)
    {
        using var resp = await HttpClients.ShortFetch.GetAsync(trackUrl, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var cues = Subtitles.ParseVttOrSrt(raw);
        if (cues.Count == 0) return null;

        if (translateToCzech)
            cues = await Subtitles.TranslateCuesToCzechAsync(cues, ct);

        return Subtitles.FormatSrt(cues);
    }

    private static string SaveCachedSrt(string key, string srtContent)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        var path = Path.Combine(Subtitles.GetCacheDirectory(), $"yt_{hash}.cs.srt");
        File.WriteAllText(path, srtContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static async Task<List<YtSearchResult>> SearchAsync(
        string exe,
        string query,
        int maxResults = 10,
        CancellationToken ct = default,
        int offset = 0,
        bool youtubeMusic = false)
    {
        if (string.IsNullOrWhiteSpace(exe))
            throw new ArgumentException("Cesta k yt-dlp.exe je prázdná.", nameof(exe));
        if (string.IsNullOrWhiteSpace(query))
            return new List<YtSearchResult>();

        int count = Math.Clamp(maxResults, 1, 50);
        int start = Math.Max(1, offset + 1);
        int end = Math.Min(500, start + count - 1);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--dump-json");
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--no-warnings");
        if (youtubeMusic || start > 1)
        {
            psi.ArgumentList.Add("--playlist-start");
            psi.ArgumentList.Add(start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--playlist-end");
            psi.ArgumentList.Add(end.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (youtubeMusic)
        {
            psi.ArgumentList.Add($"https://music.youtube.com/search?q={Uri.EscapeDataString(query.Trim())}#songs");
        }
        else
        {
            psi.ArgumentList.Add($"ytsearch{end}:{query.Trim()}");
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit yt-dlp.exe.");

        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(SearchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(p);
            await ObserveStreamsAsync(outTask, errTask);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new TimeoutException($"Vyhledávání yt-dlp překročilo limit ({(int)SearchTimeout.TotalSeconds} s).");
        }

        var stdout = await outTask;
        var stderr = await errTask;
        var results = new List<YtSearchResult>();

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var title = GetJsonString(root, "title") ?? "";
                var id = GetJsonString(root, "id") ?? "";
                var url = GetJsonString(root, "webpage_url") ?? GetJsonString(root, "url") ?? "";
                if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = (youtubeMusic ? "https://music.youtube.com/watch?v=" : "https://www.youtube.com/watch?v=") + url;
                }
                else if (url.Length == 0 && id.Length == 11)
                {
                    url = (youtubeMusic ? "https://music.youtube.com/watch?v=" : "https://www.youtube.com/watch?v=") + id;
                }

                if (url.Length == 0) continue;

                // Skip non-playable album/playlist/channel browse pages
                if (!url.Contains("watch?v=", StringComparison.OrdinalIgnoreCase) &&
                    (url.Contains("/browse/", StringComparison.OrdinalIgnoreCase) ||
                     url.Contains("/channel/", StringComparison.OrdinalIgnoreCase) ||
                     url.Contains("/user/", StringComparison.OrdinalIgnoreCase) ||
                     url.Contains("/@", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var duration = GetJsonString(root, "duration_string");
                if (string.IsNullOrEmpty(duration) && root.TryGetProperty("duration", out var durProp) &&
                    durProp.ValueKind == JsonValueKind.Number && durProp.TryGetDouble(out var secs) && secs >= 0)
                {
                    var ts = TimeSpan.FromSeconds(secs);
                    duration = ts.TotalHours >= 1
                        ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                        : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
                }

                var channel = GetJsonString(root, "channel") ?? GetJsonString(root, "uploader") ?? "";
                if (youtubeMusic)
                {
                    if (string.IsNullOrWhiteSpace(channel)) channel = "🎵 YouTube Music";
                    if (string.IsNullOrWhiteSpace(duration)) duration = "skladba";
                }
                results.Add(new YtSearchResult(title.Length > 0 ? title : url, url, duration ?? "", channel));
            }
            catch (JsonException) { }
        }

        if (p.ExitCode != 0 && results.Count == 0 && !youtubeMusic)
        {
            var err = stderr.Trim();
            throw new InvalidOperationException("yt-dlp vyhledávání selhalo: " + (err.Length > 0 ? err : $"kód {p.ExitCode}"));
        }

        // Fallback for YouTube Music if #songs shelf had fewer items on a deeper page
        if (youtubeMusic && results.Count == 0)
        {
            var fallback = await SearchAsync(exe, query, maxResults, ct, offset, youtubeMusic: false);
            return fallback
                .Select(r => r with
                {
                    Url = r.Url.Replace("www.youtube.com/", "music.youtube.com/", StringComparison.OrdinalIgnoreCase),
                    Channel = string.IsNullOrWhiteSpace(r.Channel) ? "🎵 YouTube Music" : r.Channel,
                })
                .ToList();
        }

        return results;
    }

    internal static string? ExtractDirectMediaUrl(JsonElement root)
    {
        var direct = GetJsonString(root, "url");
        if (IsPlayableStreamUrl(direct)) return direct;

        if (root.TryGetProperty("requested_downloads", out var reqDl) &&
            reqDl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqDl.EnumerateArray())
            {
                var u = GetJsonString(item, "url");
                if (IsPlayableStreamUrl(u)) return u;
            }
        }

        if (root.TryGetProperty("requested_formats", out var reqFmt) &&
            reqFmt.ValueKind == JsonValueKind.Array)
        {
            string? audioUrl = null, anyUrl = null;
            foreach (var item in reqFmt.EnumerateArray())
            {
                var u = GetJsonString(item, "url");
                if (!IsPlayableStreamUrl(u)) continue;
                var acodec = GetJsonString(item, "acodec") ?? "";
                var vcodec = GetJsonString(item, "vcodec") ?? "";
                if (!acodec.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !vcodec.Equals("none", StringComparison.OrdinalIgnoreCase))
                    return u;
                if (!acodec.Equals("none", StringComparison.OrdinalIgnoreCase) && audioUrl == null)
                    audioUrl = u;
                anyUrl ??= u;
            }
            if (audioUrl != null) return audioUrl;
            if (anyUrl != null) return anyUrl;
        }

        if (root.TryGetProperty("formats", out var formats) &&
            formats.ValueKind == JsonValueKind.Array)
        {
            string? bestProgressiveMp4 = null;
            string? bestWithAudio = null;
            string? bestAny = null;

            foreach (var fmt in formats.EnumerateArray())
            {
                var ext = GetJsonString(fmt, "ext") ?? "";
                var protocol = GetJsonString(fmt, "protocol") ?? "";
                if (ext.Equals("mhtml", StringComparison.OrdinalIgnoreCase) ||
                    protocol.Equals("mhtml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var u = GetJsonString(fmt, "url");
                if (!IsPlayableStreamUrl(u)) continue;

                var acodec = GetJsonString(fmt, "acodec") ?? "";
                var vcodec = GetJsonString(fmt, "vcodec") ?? "";
                bool hasAudio = !string.IsNullOrEmpty(acodec) && !acodec.Equals("none", StringComparison.OrdinalIgnoreCase);
                bool hasVideo = !string.IsNullOrEmpty(vcodec) && !vcodec.Equals("none", StringComparison.OrdinalIgnoreCase);

                if (hasAudio && hasVideo && ext.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    bestProgressiveMp4 = u;
                if (hasAudio)
                    bestWithAudio = u;
                bestAny = u;
            }

            return bestProgressiveMp4 ?? bestWithAudio ?? bestAny;
        }

        return null;
    }

    private static bool TryExtractFirstEntryWatchUrl(JsonElement root, out string? watchUrl)
    {
        watchUrl = null;
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var u = GetJsonString(entry, "webpage_url") ?? GetJsonString(entry, "url");
            var id = GetJsonString(entry, "id");
            if (!string.IsNullOrWhiteSpace(u) && u.Contains("watch?v=", StringComparison.OrdinalIgnoreCase))
            {
                watchUrl = u;
                return true;
            }
            if (!string.IsNullOrWhiteSpace(id) && id.Length == 11)
            {
                watchUrl = "https://www.youtube.com/watch?v=" + id;
                return true;
            }
        }
        return false;
    }

    private static bool IsPlayableStreamUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !url.Contains("/browse/", StringComparison.OrdinalIgnoreCase);

    private static string? GetJsonString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    private static void TryKillTree(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task ObserveStreamsAsync(Task<string> outTask, Task<string> errTask)
    {
        try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }
    }
}
