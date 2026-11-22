using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StickCast.Net;

public sealed record ScreenCaptureOptions(
    int OffsetX = 0,
    int OffsetY = 0,
    int Width = 0,
    int Height = 0,
    int Fps = 25,
    int MaxWidth = 1280,
    string? AudioDevice = null);

/// <summary>
/// Captures the Windows desktop via ffmpeg.exe (gdigrab -> H.264 MPEG-TS)
/// and streams it to an output Stream (e.g. MediaServer live stream).
/// </summary>
public sealed class ScreenStream : IDisposable
{
    private static readonly Regex QuotedDeviceRegex = new("\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly object _sync = new();
    private Process? _process;
    private CancellationTokenSource? _stopCts;
    private int _disposed;

    public Action<string>? Log { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false };
        }
    }

    public static string? FindFfmpeg() => ToolDownloader.FindTool("ffmpeg.exe");

    public static bool IsFfmpegAvailable() => FindFfmpeg() != null;

    internal static List<string> BuildArgs(ScreenCaptureOptions options)
    {
        int fps = Math.Clamp(options.Fps, 1, 60);
        int gop = Math.Max(fps, fps * 2);
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-thread_queue_size", "1024",
            "-use_wallclock_as_timestamps", "1",
            "-f", "gdigrab",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture),
        };

        if (options.Width > 0 && options.Height > 0)
        {
            args.Add("-offset_x");
            args.Add(options.OffsetX.ToString(CultureInfo.InvariantCulture));
            args.Add("-offset_y");
            args.Add(options.OffsetY.ToString(CultureInfo.InvariantCulture));
            args.Add("-video_size");
            args.Add($"{options.Width}x{options.Height}");
        }

        args.Add("-i");
        args.Add("desktop");

        bool hasAudio = !string.IsNullOrWhiteSpace(options.AudioDevice);
        if (hasAudio)
        {
            args.Add("-thread_queue_size");
            args.Add("1024");
            args.Add("-use_wallclock_as_timestamps");
            args.Add("1");
            args.Add("-f");
            args.Add("dshow");
            args.Add("-audio_buffer_size");
            args.Add("50");
            args.Add("-i");
            args.Add($"audio={options.AudioDevice}");
        }

        if (options.MaxWidth > 0)
        {
            args.Add("-vf");
            args.Add($"scale='min({options.MaxWidth},iw)':-2");
        }

        args.AddRange(new[]
        {
            "-fps_mode", "cfr",
            "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-g", gop.ToString(CultureInfo.InvariantCulture),
            "-maxrate", "4500k",
            "-bufsize", "9000k",
            "-pix_fmt", "yuv420p",
        });

        if (hasAudio)
        {
            args.AddRange(new[]
            {
                "-af", "aresample=async=1:first_pts=0",
                "-c:a", "aac",
                "-ar", "44100",
                "-ac", "2",
                "-b:a", "128k",
            });
        }

        args.Add("-muxdelay");
        args.Add("0");
        args.Add("-f");
        args.Add("mpegts");
        args.Add("pipe:1");

        return args;
    }

    public static async Task<List<string>> ListAudioDevicesAsync(CancellationToken ct = default)
    {
        var exe = FindFfmpeg();
        if (exe == null) return new List<string>();

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null) return new List<string>();

        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
        }

        string stderr = "";
        try { stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        try { await outTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        return ParseAudioDevices(stderr);
    }

    internal static List<string> ParseAudioDevices(string? stderr)
    {
        var devices = new List<string>();
        if (string.IsNullOrWhiteSpace(stderr)) return devices;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inAudioSection = false;

        foreach (var rawLine in stderr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isTaggedAudio = line.Contains("(audio)", StringComparison.OrdinalIgnoreCase);
            bool isTaggedOther = line.Contains("(video)", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("(none)", StringComparison.OrdinalIgnoreCase);

            if (!isTaggedAudio && (!inAudioSection || isTaggedOther))
                continue;

            var m = QuotedDeviceRegex.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length > 0 && seen.Add(name))
                    devices.Add(name);
            }
        }

        return devices;
    }

    public Task StreamToAsync(Stream output, CancellationToken ct) =>
        StreamToAsync(output, new ScreenCaptureOptions(), ct);

    public async Task StreamToAsync(Stream output, ScreenCaptureOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        var exe = FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg.exe nebyl nalezen vedle aplikace ani v PATH.");

        Stop();

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArgs(options))
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit ffmpeg.exe.");

        var stopCts = new CancellationTokenSource();
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                TryKillTree(proc);
                proc.Dispose();
                stopCts.Dispose();
                throw new ObjectDisposedException(nameof(ScreenStream));
            }
            _process = proc;
            _stopCts = stopCts;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stopCts.Token);
        var stderrTask = DrainStderrAsync(proc, linkedCts.Token);

        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(output, 65536, linkedCts.Token);
            await output.FlushAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* client disconnected */ }
        finally
        {
            TryKillTree(proc);
            try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            lock (_sync)
            {
                if (ReferenceEquals(_process, proc)) _process = null;
                if (ReferenceEquals(_stopCts, stopCts)) _stopCts = null;
            }
            stopCts.Dispose();
            proc.Dispose();
        }
    }

    public void Stop()
    {
        Process? proc;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            proc = _process;
            cts = _stopCts;
        }

        try { cts?.Cancel(); } catch { }
        if (proc != null) TryKillTree(proc);
    }

    private async Task DrainStderrAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                    Log?.Invoke($"ffmpeg: {line}");
            }
        }
        catch { }
    }

    private static void TryKillTree(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}
