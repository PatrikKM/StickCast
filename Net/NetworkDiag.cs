using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace StickCast.Net;

public sealed record FirewallDiagResult(
    bool PortListening,
    bool FirewallRulePresent,
    string LocalIp,
    int Port,
    string Summary,
    string? Advice);

/// <summary>Network, Windows Firewall, and Wi-Fi diagnostics/helpers for StickCast.</summary>
public static class NetworkDiag
{
    private static readonly Regex SsidLineRegex =
        new(@"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StateLineRegex =
        new(@"^\s*(?:State|Stav)\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<FirewallDiagResult> CheckMediaServerReachabilityAsync(
        int port, IPAddress localIp, CancellationToken ct = default)
    {
        bool listening = await CheckTcpPortAsync(localIp, port, ct);
        bool rulePresent = false;

        try
        {
            var (exit, stdout, _) = await RunNetshAsync("advfirewall firewall show rule name=all dir=in", ct);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                rulePresent = ParseFirewallRuleMatch(stdout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore netsh query failures; report based on port check.
        }

        string ipText = localIp.ToString();
        string summary;
        string? advice = null;

        if (!listening)
        {
            summary = $"Mediální server na {ipText}:{port} neodpovídá.";
            advice = "Restartujte aplikaci nebo zkontrolujte log mediálního serveru.";
        }
        else if (rulePresent)
        {
            summary = $"Port {ipText}:{port} je dostupný a pravidlo firewallu pro StickCast je aktivní.";
        }
        else
        {
            summary = $"Port {ipText}:{port} naslouchá, ale ve Windows Firewallu nebylo nalezeno povolení pro StickCast.";
            advice = "Pokud stick nepřehrává lokální soubory, klikněte na „Povolit ve Firewallu“.";
        }

        return new FirewallDiagResult(listening, rulePresent, ipText, port, summary, advice);
    }

    public static async Task<bool> TryAddFirewallRuleAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        var args = $"advfirewall firewall add rule name=\"StickCast MediaServer\" dir=in action=allow program=\"{exePath}\" enable=yes profile=private";
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
            });
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception)
        {
            // User declined UAC prompt or netsh unavailable.
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> GetCurrentWifiSsidAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, stdout, _) = await RunNetshAsync("wlan show interfaces", ct);
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            return ParseWifiSsid(stdout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(bool Ok, string Message)> ConnectToWifiAsync(
        string ssid, string? password = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ssid))
            return (false, "Zadejte název Wi-Fi sítě (SSID).");

        var cleanSsid = ssid.Trim();
        var (exit, stdout, stderr) = await RunNetshAsync($"wlan connect name=\"{cleanSsid}\"", ct);
        if (exit == 0)
            return (true, $"Připojeno k Wi-Fi „{cleanSsid}“.");

        if (string.IsNullOrEmpty(password))
        {
            var err = FirstLine(stderr, stdout);
            return (false, string.IsNullOrEmpty(err)
                ? $"Nepodařilo se připojit k „{cleanSsid}“."
                : $"Nepodařilo se připojit k „{cleanSsid}“: {err}");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"stickcast_wifi_{Guid.NewGuid():N}.xml");
        try
        {
            var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(cleanSsid));
            var escSsid = SecurityElement.Escape(cleanSsid);
            var escPass = SecurityElement.Escape(password);

            var profileXml =
                "<?xml version=\"1.0\"?>\n" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\n" +
                $"  <name>{escSsid}</name>\n" +
                "  <SSIDConfig>\n" +
                "    <SSID>\n" +
                $"      <hex>{hex}</hex>\n" +
                $"      <name>{escSsid}</name>\n" +
                "    </SSID>\n" +
                "  </SSIDConfig>\n" +
                "  <connectionType>ESS</connectionType>\n" +
                "  <connectionMode>manual</connectionMode>\n" +
                "  <MSM>\n" +
                "    <security>\n" +
                "      <authEncryption>\n" +
                "        <authentication>WPA2PSK</authentication>\n" +
                "        <encryption>AES</encryption>\n" +
                "        <useOneX>false</useOneX>\n" +
                "      </authEncryption>\n" +
                "      <sharedKey>\n" +
                "        <keyType>passPhrase</keyType>\n" +
                "        <protected>false</protected>\n" +
                $"        <keyMaterial>{escPass}</keyMaterial>\n" +
                "      </sharedKey>\n" +
                "    </security>\n" +
                "  </MSM>\n" +
                "</WLANProfile>\n";

            await File.WriteAllTextAsync(tempPath, profileXml, Encoding.UTF8, ct);

            var (addExit, addOut, addErr) = await RunNetshAsync($"wlan add profile filename=\"{tempPath}\" user=current", ct);
            if (addExit != 0)
            {
                var err = FirstLine(addErr, addOut);
                return (false, $"Nelze přidat Wi-Fi profil „{cleanSsid}“: {err}");
            }

            var (connExit, connOut, connErr) = await RunNetshAsync($"wlan connect name=\"{cleanSsid}\"", ct);
            if (connExit == 0)
                return (true, $"Připojeno k Wi-Fi „{cleanSsid}“.");

            var cErr = FirstLine(connErr, connOut);
            return (false, $"Profil přidán, ale připojení k „{cleanSsid}“ selhalo: {cErr}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* ignore cleanup failure */ }
        }
    }

    internal static string? ParseWifiSsid(string? netshOutput)
    {
        if (string.IsNullOrWhiteSpace(netshOutput)) return null;

        var stateMatch = StateLineRegex.Match(netshOutput);
        if (stateMatch.Success)
        {
            var state = stateMatch.Groups[1].Value.Trim();
            if (state.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
                state.Contains("odpojen", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var ssidMatch = SsidLineRegex.Match(netshOutput);
        if (!ssidMatch.Success) return null;

        var ssid = ssidMatch.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(ssid) ? null : ssid;
    }

    internal static bool ParseFirewallRuleMatch(string? netshOutput, string appKeyword = "StickCast")
    {
        if (string.IsNullOrWhiteSpace(netshOutput)) return false;

        var blocks = Regex.Split(netshOutput, @"(?:\r?\n){2,}");
        foreach (var block in blocks)
        {
            if (!block.Contains(appKeyword, StringComparison.OrdinalIgnoreCase)) continue;

            bool isEnabled = true;
            bool isAllow = false;

            foreach (var rawLine in block.Split('\n'))
            {
                var line = rawLine.Trim();
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line[..colon].Trim();
                var val = line[(colon + 1)..].Trim();

                if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Povoleno", StringComparison.OrdinalIgnoreCase))
                {
                    isEnabled = val.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                                val.Equals("Ano", StringComparison.OrdinalIgnoreCase);
                }
                else if (key.Equals("Action", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Akce", StringComparison.OrdinalIgnoreCase))
                {
                    isAllow = val.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                              val.Equals("Povolit", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (isEnabled && isAllow) return true;
        }

        return false;
    }

    private static async Task<bool> CheckTcpPortAsync(IPAddress ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(ip, port, timeoutCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunNetshAsync(string arguments, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static string FirstLine(string? a, string? b)
    {
        var s = !string.IsNullOrWhiteSpace(a) ? a : b;
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Trim().Split('\n')[0].Trim();
    }
}
