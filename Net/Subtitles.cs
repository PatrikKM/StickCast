using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StickCast.Net;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Helpers for discovering local Czech subtitles, converting WebVTT (including YouTube auto-captions)
/// to clean SRT, and translating foreign subtitles to Czech via Google Translate when needed.
/// </summary>
internal static class Subtitles
{
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CueArrowRegex = new(
        @"^(?<start>(?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)\s*-->\s*(?<end>(?:\d{1,2}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)",
        RegexOptions.Compiled);

    private static readonly string[] LocalSubtitleSuffixes =
    {
        ".cs.srt", ".cz.srt", ".ces.srt", ".cze.srt", ".srt",
        ".cs.vtt", ".cz.vtt", ".ces.vtt", ".cze.vtt", ".vtt",
    };

    public static string GetCacheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StickCast", "subs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Finds a matching subtitle file next to a local video, preferring Czech (.cs/.cz) and .srt.
    /// Converts .vtt to a cached .srt file so both DLNA renderers (Samsung TV) and ffmpeg work cleanly.
    /// </summary>
    public static string? FindLocalSubtitleSrt(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return null;
        var dir = Path.GetDirectoryName(mediaPath);
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

        foreach (var suffix in LocalSubtitleSuffixes)
        {
            var candidate = Path.Combine(dir, baseName + suffix);
            if (!File.Exists(candidate)) continue;

            try
            {
                var subText = File.ReadAllText(candidate, Encoding.UTF8);
                var cues = ParseVttOrSrt(subText);
                if (cues.Count == 0) continue;

                var cached = Path.Combine(GetCacheDirectory(), $"{baseName}_{Math.Abs(candidate.GetHashCode()):X8}_ylw.srt");
                File.WriteAllText(cached, FormatSrt(cues), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return cached;
            }
            catch
            {
                return candidate;
            }
        }

        return null;
    }

    public static List<SubtitleCue> ParseVttOrSrt(string? content)
    {
        var raw = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(content)) return raw;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                           .Replace('\r', '\n')
                           .Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                while (i < lines.Length && lines[i].Trim().Length > 0) i++;
                continue;
            }

            var match = CueArrowRegex.Match(line);
            if (!match.Success)
            {
                // Skip optional numeric index line (SRT) or cue identifier (VTT)
                if (i + 1 < lines.Length && CueArrowRegex.IsMatch(lines[i + 1].Trim()))
                {
                    i++;
                    match = CueArrowRegex.Match(lines[i].Trim());
                }
                else
                {
                    i++;
                    continue;
                }
            }

            if (!TryParseTimestamp(match.Groups["start"].Value, out var start) ||
                !TryParseTimestamp(match.Groups["end"].Value, out var end))
            {
                i++;
                continue;
            }

            i++;
            var textLines = new List<string>();
            while (i < lines.Length)
            {
                var t = lines[i];
                if (string.IsNullOrWhiteSpace(t)) break;
                var cleaned = CleanCueLine(t);
                if (cleaned.Length > 0)
                    textLines.Add(cleaned);
                i++;
            }

            if (textLines.Count > 0 && end > start)
            {
                // Ignore tiny <120ms transition duplicates typical for YouTube rolling auto-captions
                if (end - start >= TimeSpan.FromMilliseconds(120))
                    raw.Add(new SubtitleCue(start, end, string.Join("\n", textLines)));
            }
        }

        return DeduplicateRollingCues(raw);
    }

    private static List<SubtitleCue> DeduplicateRollingCues(List<SubtitleCue> cues)
    {
        if (cues.Count <= 1) return cues;
        var result = new List<SubtitleCue>(cues.Count);
        string? prevLastLine = null;

        foreach (var cue in cues)
        {
            var parts = cue.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && prevLastLine != null &&
                parts[0].Equals(prevLastLine, StringComparison.Ordinal))
            {
                parts = parts[1..];
            }

            var joined = string.Join("\n", parts).Trim();
            if (joined.Length == 0) continue;

            if (result.Count > 0 &&
                result[^1].Text.Equals(joined, StringComparison.Ordinal) &&
                cue.Start - result[^1].End <= TimeSpan.FromMilliseconds(350))
            {
                result[^1] = result[^1] with { End = cue.End };
            }
            else
            {
                result.Add(cue with { Text = joined });
            }

            prevLastLine = parts[^1];
        }

        return result;
    }

    private static string CleanCueLine(string line)
    {
        var noTags = TagRegex.Replace(line, "");
        var decoded = WebUtility.HtmlDecode(noTags);
        return decoded.Trim();
    }

    public static string FormatSrt(IReadOnlyList<SubtitleCue> cues, bool yellowBold = true)
    {
        var sb = new StringBuilder();
        for (int idx = 0; idx < cues.Count; idx++)
        {
            var c = cues[idx];
            sb.Append(idx + 1).Append("\r\n");
            sb.Append(FormatSrtTime(c.Start)).Append(" --> ").Append(FormatSrtTime(c.End)).Append("\r\n");
            var body = c.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            if (yellowBold)
                sb.Append("<font color=\"#FFFF00\"><b>").Append(body).Append("</b></font>");
            else
                sb.Append(body);
            sb.Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string FormatSrtTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    private static bool TryParseTimestamp(string raw, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var s = raw.Trim().Replace(',', '.');
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return false;

        int h = 0, m;
        double sec;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) return false;
        }
        else
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) return false;
        }

        result = TimeSpan.FromSeconds(h * 3600.0 + m * 60.0 + sec);
        return true;
    }

    public static async Task<List<SubtitleCue>> TranslateCuesToCzechAsync(
        IReadOnlyList<SubtitleCue> cues,
        CancellationToken ct = default)
    {
        if (cues.Count == 0) return new List<SubtitleCue>();

        const int batchSize = 25;
        const string marker = "|||";
        var translated = new List<SubtitleCue>(cues.Count);

        for (int offset = 0; offset < cues.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(batchSize, cues.Count - offset);
            var batch = new List<SubtitleCue>(count);
            for (int j = 0; j < count; j++) batch.Add(cues[offset + j]);

            var joined = string.Join($"\n{marker}\n", batch.Select(c => c.Text.Replace("\n", " ", StringComparison.Ordinal)));
            var translatedText = await TranslateTextToCzechAsync(joined, ct);
            var split = translatedText?.Split(marker, StringSplitOptions.None);

            if (split != null && split.Length == batch.Count)
            {
                for (int j = 0; j < batch.Count; j++)
                {
                    var text = split[j].Trim();
                    translated.Add(batch[j] with { Text = text.Length > 0 ? text : batch[j].Text });
                }
            }
            else
            {
                // Fallback per line if separator count changed
                foreach (var cue in batch)
                {
                    var single = await TranslateTextToCzechAsync(cue.Text.Replace("\n", " ", StringComparison.Ordinal), ct);
                    translated.Add(cue with { Text = !string.IsNullOrWhiteSpace(single) ? single.Trim() : cue.Text });
                }
            }
        }

        return translated;
    }

    private static async Task<string?> TranslateTextToCzechAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        try
        {
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client", "gtx"),
                new KeyValuePair<string, string>("sl", "auto"),
                new KeyValuePair<string, string>("tl", "cs"),
                new KeyValuePair<string, string>("dt", "t"),
                new KeyValuePair<string, string>("q", text),
            });

            using var resp = await HttpClients.ShortFetch.PostAsync("https://translate.googleapis.com/translate_a/single", form, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var segments = doc.RootElement[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            var sb = new StringBuilder();
            foreach (var seg in segments.EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                    seg[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(seg[0].GetString());
                }
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
}
