using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StickCast.Net;

/// <summary>Minimal SSDP (UPnP discovery) client.</summary>
internal static class Ssdp
{
    private static readonly IPAddress MulticastAddr = IPAddress.Parse("239.255.255.250");
    private const int SioUdpConnReset = -1744830452;

    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:MediaRenderer:1",
        "urn:schemas-upnp-org:service:AVTransport:1",
        "ssdp:all",
    };

    /// <summary>Returns the deduplicated list of LOCATION URLs (device description XMLs) that answered.</summary>
    /// <param name="unicastTarget">If set, M-SEARCH is sent directly to this IP instead of multicast.</param>
    public static async Task<List<string>> SearchAsync(TimeSpan timeout, IPAddress? unicastTarget = null,
        Action<string>? log = null, int port = 1900, CancellationToken ct = default)
    {
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUdns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        var gate = new object();

        List<IPAddress> interfaces;
        if (unicastTarget != null)
        {
            interfaces = new List<IPAddress> { NetUtil.GetLocalIpFor(unicastTarget) };
        }
        else
        {
            interfaces = NetUtil.GetLocalIPv4();
            if (interfaces.Count == 0) interfaces.Add(IPAddress.Any);
        }

        var tasks = interfaces.Select(ip =>
            SearchOnInterfaceAsync(ip, timeout, unicastTarget, port, seenLocations, seenUdns, results, gate, log, ct));
        await Task.WhenAll(tasks);
        lock (gate) return new List<string>(results);
    }

    private static async Task SearchOnInterfaceAsync(
        IPAddress local,
        TimeSpan timeout,
        IPAddress? unicast,
        int port,
        HashSet<string> seenLocations,
        HashSet<string> seenUdns,
        List<string> results,
        object gate,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            if (OperatingSystem.IsWindows())
            {
                try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); }
                catch { /* ignore on sockets that do not support it */ }
            }

            if (unicast == null)
            {
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                if (!local.Equals(IPAddress.Any))
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        local.GetAddressBytes());
                }
            }

            var target = new IPEndPoint(unicast ?? MulticastAddr, port);
            var hostHeader = unicast != null ? $"{unicast}:{port}" : $"239.255.255.250:{port}";
            var mx = Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 1, 5);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var receiveTask = ReceiveLoopAsync(udp, seenLocations, seenUdns, results, gate, log, cts.Token);

            // Send twice – UDP on Wi-Fi is lossy and cheap dongles sometimes miss the first one.
            for (int round = 0; round < 2 && !cts.IsCancellationRequested; round++)
            {
                foreach (var st in SearchTargets)
                {
                    if (cts.IsCancellationRequested) break;
                    var msg = "M-SEARCH * HTTP/1.1\r\n" +
                              $"HOST: {hostHeader}\r\n" +
                              "MAN: \"ssdp:discover\"\r\n" +
                              $"MX: {mx}\r\n" +
                              $"ST: {st}\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(msg);
                    try { await udp.SendAsync(bytes, bytes.Length, target); }
                    catch (SocketException ex) when (round == 0)
                    {
                        log?.Invoke($"SSDP odeslání na {local} selhalo: {ex.Message}");
                        break;
                    }
                }
                if (round == 0)
                {
                    try { await Task.Delay(150, cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            await receiveTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"SSDP na rozhraní {local} selhalo: {ex.Message}");
        }
    }

    private static async Task ReceiveLoopAsync(
        UdpClient udp,
        HashSet<string> seenLocations,
        HashSet<string> seenUdns,
        List<string> results,
        object gate,
        Action<string>? log,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            var text = Encoding.UTF8.GetString(res.Buffer);
            if (!TryParseResponse(text, out var location, out var udn)) continue;

            bool added = false;
            lock (gate)
            {
                if (!seenLocations.Contains(location) &&
                    (string.IsNullOrEmpty(udn) || !seenUdns.Contains(udn)))
                {
                    seenLocations.Add(location);
                    if (!string.IsNullOrEmpty(udn)) seenUdns.Add(udn);
                    results.Add(location);
                    added = true;
                }
            }
            if (added) log?.Invoke($"SSDP odpověď z {res.RemoteEndPoint.Address}: {location}");
        }
    }

    internal static bool TryParseResponse(string text, out string location, out string udn)
    {
        location = "";
        udn = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Split('\n');
        var firstLine = lines[0].Trim('\r', ' ', '\t');
        if (!firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) &&
            !firstLine.StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) &&
            !firstLine.Contains(" 200 ", StringComparison.Ordinal))
        {
            return false;
        }

        string? rawLoc = null;
        string? rawUsn = null;
        string? nts = null;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim('\r');
            if (string.IsNullOrWhiteSpace(line)) break;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                rawLoc ??= value;
            else if (name.Equals("USN", StringComparison.OrdinalIgnoreCase))
                rawUsn ??= value;
            else if (name.Equals("NTS", StringComparison.OrdinalIgnoreCase))
                nts ??= value;
        }

        if (string.Equals(nts, "ssdp:byebye", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(rawLoc) ||
            !Uri.TryCreate(rawLoc, UriKind.Absolute, out var locUri) ||
            (locUri.Scheme != Uri.UriSchemeHttp && locUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        location = locUri.AbsoluteUri;
        udn = ExtractUdn(rawUsn);
        return true;
    }

    internal static string ExtractUdn(string? usn)
    {
        if (string.IsNullOrWhiteSpace(usn)) return "";
        var trimmed = usn.Trim();
        var sep = trimmed.IndexOf("::", StringComparison.Ordinal);
        return (sep > 0 ? trimmed[..sep] : trimmed).Trim();
    }
}
