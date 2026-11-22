using System.Diagnostics;

namespace StickCast.Net;

/// <summary>
/// Transcodes incompatible video (and optionally burns in .srt/.vtt subtitles)
/// to H.264 + AAC MPEG-TS via ffmpeg.exe and streams it to an output Stream.
/// </summary>
public sealed class TranscodeStream : IDisposable
{
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

    internal const string YellowSubtitleStyle =
        "FontName=Segoe UI,FontSize=24,Bold=1,PrimaryColour=&H0000FFFF,OutlineColour=&H00000000,BackColour=&H80000000,BorderStyle=1,Outline=2.5,Shadow=1.5,Alignment=2,MarginV=24";

    private const string HttpUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    internal static List<string> BuildArgs(string inputPathOrUrl, string? subtitlePath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-fflags", "+genpts",
        };

        if (inputPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            inputPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[]
            {
                "-user_agent", HttpUserAgent,
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "2",
            });
            if (inputPathOrUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                inputPathOrUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "-headers", "Referer: https://www.youtube.com/\r\nOrigin: https://www.youtube.com\r\n" });
            }
        }
        else
        {
            args.Add("-re");
        }

        args.Add("-i");
        args.Add(inputPathOrUrl);

        var vf = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
        {
            var escaped = subtitlePath
                .Replace("\\", "/", StringComparison.Ordinal)
                .Replace(":", "\\:", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
            vf = $"{vf},subtitles='{escaped}':charenc=UTF-8:force_style='{YellowSubtitleStyle}'";
        }
        else if (!string.IsNullOrWhiteSpace(subtitlePath) && !Path.IsPathRooted(subtitlePath))
        {
            // Support relative paths in unit tests
            var escaped = subtitlePath
                .Replace("\\", "/", StringComparison.Ordinal)
                .Replace(":", "\\:", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
            vf = $"{vf},subtitles='{escaped}':charenc=UTF-8:force_style='{YellowSubtitleStyle}'";
        }
        args.Add("-vf");
        args.Add(vf);

        args.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-g", "50",
            "-pix_fmt", "yuv420p",
            "-af", "aresample=async=1:first_pts=0",
            "-c:a", "aac",
            "-ar", "44100",
            "-b:a", "192k",
            "-ac", "2",
            "-avoid_negative_ts", "make_zero",
            "-f", "mpegts",
            "pipe:1",
        });

        return args;
    }

    public async Task StreamToAsync(string inputPathOrUrl, string? subtitlePath, Stream output, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(inputPathOrUrl))
            throw new ArgumentException("Vstupní cesta nebo URL je prázdná.", nameof(inputPathOrUrl));
        ArgumentNullException.ThrowIfNull(output);

        var exe = ScreenStream.FindFfmpeg()
            ?? throw new FileNotFoundException("ffmpeg.exe nebyl nalezen.");

        Stop();

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArgs(inputPathOrUrl, subtitlePath))
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
                throw new ObjectDisposedException(nameof(TranscodeStream));
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
                if (line.Length > 0)
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
