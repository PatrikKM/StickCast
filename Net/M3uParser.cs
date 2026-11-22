using System.Text;

namespace StickCast.Net;

public sealed record M3uEntry(string Name, string Url);

/// <summary>
/// Parser for IPTV .m3u / .m3u8 channel playlists (#EXTM3U / #EXTINF).
/// </summary>
public static class M3uParser
{
    public static bool IsChannelList(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        // HLS segment playlists have target duration / media sequence; IPTV channel lists do not.
        if (content.Contains("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase))
            return false;

        return content.Contains("#EXTINF:", StringComparison.OrdinalIgnoreCase) &&
               Parse(content).Count > 0;
    }

    public static List<M3uEntry> Parse(string? content)
    {
        var result = new List<M3uEntry>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        string? pendingName = null;
        var lines = content.TrimStart('\uFEFF').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingName = ExtractExtInfTitle(line);
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            var name = !string.IsNullOrWhiteSpace(pendingName) ? pendingName : FallbackName(line);
            result.Add(new M3uEntry(name, line));
            pendingName = null;
        }

        return result;
    }

    public static string Format(IEnumerable<M3uEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Url)) continue;
            var safeName = (entry.Name ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (safeName.Length == 0) safeName = FallbackName(entry.Url);
            var safeUrl = entry.Url.Replace("\r", "").Replace("\n", "").Trim();
            sb.Append("#EXTINF:-1,").AppendLine(safeName);
            sb.AppendLine(safeUrl);
        }
        return sb.ToString();
    }

    private static string? ExtractExtInfTitle(string line)
    {
        bool inQuotes = false;
        for (int i = 8; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                var title = line[(i + 1)..].Trim();
                return title.Length > 0 ? title : null;
            }
        }
        return null;
    }

    private static string FallbackName(string url)
    {
        try
        {
            var u = new Uri(url);
            var file = Path.GetFileName(u.AbsolutePath);
            return string.IsNullOrEmpty(file) ? u.Host : Uri.UnescapeDataString(file);
        }
        catch
        {
            return url;
        }
    }
}
