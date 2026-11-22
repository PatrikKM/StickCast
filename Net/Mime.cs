namespace StickCast.Net;

internal static class Mime
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mkv"] = "video/x-mkv",
        [".avi"] = "video/x-msvideo",
        [".divx"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv",
        [".asf"] = "video/x-ms-asf",
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".mts"] = "video/mp2t",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".m2v"] = "video/mpeg",
        [".vob"] = "video/mpeg",
        [".webm"] = "video/webm",
        [".flv"] = "video/x-flv",
        [".3gp"] = "video/3gpp",
        [".3g2"] = "video/3gpp2",
        [".ogv"] = "video/ogg",
        [".m3u8"] = "application/vnd.apple.mpegurl",

        // Audio
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".wma"] = "audio/x-ms-wma",
        [".ac3"] = "audio/ac3",
        [".aiff"] = "audio/aiff",
        [".aif"] = "audio/aiff",
        [".amr"] = "audio/amr",

        // Image
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

    private static readonly Dictionary<string, string> SubtitleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".srt"] = "application/x-subrip",
        [".vtt"] = "text/vtt",
    };

    public static readonly string FileDialogFilter =
        "Média|" + string.Join(";", Map.Keys.Where(k => k != ".m3u8").Select(k => "*" + k)) + "|Všechny soubory|*.*";

    public static bool IsKnownExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && Map.ContainsKey(ext);

    public static string FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "video/mp4";
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return "video/mp4";
        if (Map.TryGetValue(ext, out var m)) return m;
        if (SubtitleMap.TryGetValue(ext, out var s)) return s;
        return "video/mp4";
    }

    public static string FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "video/mp4";
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrEmpty(ext))
            {
                if (Map.TryGetValue(ext, out var m)) return m;
                if (SubtitleMap.TryGetValue(ext, out var s)) return s;
            }
        }
        catch { }
        return "video/mp4";
    }

    public static bool UrlLooksLikeMedia(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            return !string.IsNullOrEmpty(ext) && Map.ContainsKey(ext);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns the 4th field of DLNA protocolInfo / contentFeatures.dlna.org header
    /// with DLNA.ORG_PN profile where appropriate for common formats.
    /// </summary>
    public static string DlnaFeatures(string? mime)
    {
        var m = (mime ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (m.StartsWith("image/", StringComparison.Ordinal))
        {
            const string imgFlags = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=00f00000000000000000000000000000";
            return m switch
            {
                "image/jpeg" => "DLNA.ORG_PN=JPEG_LRG;" + imgFlags,
                "image/png" => "DLNA.ORG_PN=PNG_LRG;" + imgFlags,
                _ => imgFlags,
            };
        }

        const string avFlags = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        return m switch
        {
            "video/mp4" => "DLNA.ORG_PN=AVC_MP4_MP_SD_AAC_MULT5;" + avFlags,
            "video/mpeg" => "DLNA.ORG_PN=MPEG_PS_PAL;" + avFlags,
            "video/mp2t" => "DLNA.ORG_PN=MPEG_TS_SD_EU_ISO;" + avFlags,
            "video/x-ms-wmv" => "DLNA.ORG_PN=WMVMED_BASE;" + avFlags,
            "audio/mpeg" => "DLNA.ORG_PN=MP3;" + avFlags,
            "audio/mp4" => "DLNA.ORG_PN=AAC_ISO_320;" + avFlags,
            "audio/x-ms-wma" => "DLNA.ORG_PN=WMABASE;" + avFlags,
            _ => avFlags,
        };
    }
}
