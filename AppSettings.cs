using System.Text.Json;

namespace StickCast;

public sealed record UrlEntry(string Url, string Title, string? Timestamp = null);

public sealed class AppSettings
{
    public string? LastLocation { get; set; }
    /// <summary>Friendly name of the last renderer – used to re-find it when its IP/port changes.</summary>
    public string? LastDeviceName { get; set; }
    public List<string> RecentUrls { get; set; } = new();
    public List<UrlEntry> HistoryEntries { get; set; } = new();
    public List<UrlEntry> FavoriteEntries { get; set; } = new();
    public int Volume { get; set; } = 30;
    public bool AutoNext { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public int SlideshowSeconds { get; set; }
    public bool TranscodeMode { get; set; }
    public string RepeatMode { get; set; } = "none";
    public bool Shuffle { get; set; }
    public bool AutoCzechSubtitles { get; set; } = true;
    public bool BurnSubtitles { get; set; }
    public bool YtAutoPlayNext { get; set; } = true;
    public bool LightTheme { get; set; }
    public bool DirectPlayDefaultApplied { get; set; }
    public string HotspotSsid { get; set; } = "WifiDisplay 28C228F2";
    public string HotspotPassword { get; set; } = "12345678";
    public string LastAdbEndpoint { get; set; } = "";

    // Window placement (NaN = not saved yet)
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly object SaveLock = new();

    internal static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickCast", "settings.json");

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
        }
        catch { /* corrupted file → defaults */ }
        s ??= new AppSettings();
        s.Normalize();
        return s;
    }

    private void Normalize()
    {
        RecentUrls = (RecentUrls ?? new())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();

        HistoryEntries = (HistoryEntries ?? new())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Url))
            .Select(e => new UrlEntry(e.Url.Trim(), string.IsNullOrWhiteSpace(e.Title) ? e.Url.Trim() : e.Title.Trim(), e.Timestamp))
            .DistinctBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        if (HistoryEntries.Count == 0 && RecentUrls.Count > 0)
        {
            foreach (var u in RecentUrls)
                HistoryEntries.Add(new UrlEntry(u, u));
        }

        FavoriteEntries = (FavoriteEntries ?? new())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Url))
            .Select(e => new UrlEntry(e.Url.Trim(), string.IsNullOrWhiteSpace(e.Title) ? e.Url.Trim() : e.Title.Trim()))
            .DistinctBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        if (!DirectPlayDefaultApplied)
        {
            BurnSubtitles = false;
            TranscodeMode = false;
            DirectPlayDefaultApplied = true;
        }
        Volume = Math.Clamp(Volume, 0, 100);
        if (SlideshowSeconds is not (0 or 5 or 10)) SlideshowSeconds = 0;
        if (RepeatMode is not ("none" or "all" or "one")) RepeatMode = "none";
        if (string.IsNullOrWhiteSpace(HotspotSsid)) HotspotSsid = "WifiDisplay 28C228F2";
        HotspotPassword ??= "12345678";
        if (string.IsNullOrWhiteSpace(LastLocation) || !Uri.TryCreate(LastLocation, UriKind.Absolute, out _))
            LastLocation = null;
    }

    /// <summary>Atomic save (temp file + replace) so a crash never leaves a half-written settings file.</summary>
    public void Save()
    {
        try
        {
            lock (SaveLock)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { /* settings are best-effort */ }
    }
}
