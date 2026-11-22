using System.Diagnostics;
using System.Text;
using StickCast.Net;

namespace StickCast.Services;

/// <summary>
/// Manages ADB wireless debugging (pair / connect / disconnect / devices)
/// and launching scrcpy for mirroring Android phones onto the PC.
/// </summary>
internal static class AdbController
{
    public static string? FindExecutable(string fileName) => ToolDownloader.FindTool(fileName);

    public static async Task<(bool Ok, string Output)> RunAdbAsync(CancellationToken ct, params string[] args)
    {
        var adb = FindExecutable("adb.exe");
        if (adb == null)
            return (false, "Program adb.exe nebyl nalezen (bývá součástí balíčku scrcpy).");

        var psi = new ProcessStartInfo(adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(adb) ?? AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit adb.exe.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return (false, "Vypršel časový limit příkazu adb.");
        }

        string stdout = "", stderr = "";
        try { stdout = await outTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        try { stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        var combined = (stdout + "\n" + stderr).Trim();
        return (p.ExitCode == 0, string.IsNullOrEmpty(combined) ? $"exit {p.ExitCode}" : combined);
    }

    public static async Task<List<string>> GetOnlineDevicesAsync(CancellationToken ct)
    {
        var (ok, devsOut) = await RunAdbAsync(ct, "devices");
        if (!ok) return new List<string>();

        return devsOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Where(l => l.EndsWith("\tdevice", StringComparison.Ordinal) || l.EndsWith(" device", StringComparison.Ordinal))
            .Select(l => l.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();
    }

    public static async Task DisconnectAllAsync(CancellationToken ct)
    {
        await RunAdbAsync(ct, "disconnect");
        await RunAdbAsync(ct, "kill-server");
    }

    public static async Task LaunchScrcpyAsync(string scrcpyPath, string? endpoint, bool wifiTuning, bool noAudio, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scrcpyPath) ?? AppContext.BaseDirectory,
        };

        var cleanEndpoint = endpoint?.Trim() ?? "";
        bool hasEndpoint = !string.IsNullOrEmpty(cleanEndpoint) && cleanEndpoint.Contains(':') && !cleanEndpoint.EndsWith(':');
        var onlineDevs = await GetOnlineDevicesAsync(ct);

        if (onlineDevs.Count == 0 && hasEndpoint)
        {
            psi.ArgumentList.Add("--tcpip=" + cleanEndpoint);
        }
        else if (onlineDevs.Count > 1 && hasEndpoint && onlineDevs.Contains(cleanEndpoint, StringComparer.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(cleanEndpoint);
        }

        if (noAudio)
        {
            psi.ArgumentList.Add("--no-audio");
        }
        else if (wifiTuning)
        {
            psi.ArgumentList.Add("--audio-buffer=150");
            psi.ArgumentList.Add("--video-buffer=50");
            psi.ArgumentList.Add("--max-fps=30");
            psi.ArgumentList.Add("--video-bit-rate=6M");
        }

        Process.Start(psi);
    }
}
