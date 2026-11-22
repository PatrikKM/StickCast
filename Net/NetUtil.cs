using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StickCast.Net;

internal static class NetUtil
{
    /// <summary>All IPv4 addresses of active, multicast-capable, non-loopback interfaces.</summary>
    public static List<IPAddress> GetLocalIPv4()
    {
        var list = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }

        foreach (var ni in interfaces)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!ni.SupportsMulticast) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var addr = ua.Address;
                    if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr)) continue;
                    var bytes = addr.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    if (seen.Add(addr)) list.Add(addr);
                }
            }
            catch
            {
                // Skip interfaces whose properties cannot be queried (e.g. transient disconnect).
            }
        }
        return list;
    }

    /// <summary>Which local IP would the OS use to reach the given remote host (no packets are sent).</summary>
    public static IPAddress GetLocalIpFor(IPAddress remote)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
            return remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;

        try
        {
            using var s = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(remote, 9);
            if (s.LocalEndPoint is IPEndPoint ep)
                return ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        }
        catch
        {
            // Fallback below if routing table lookup fails.
        }

        return GetLocalIPv4().FirstOrDefault() ?? IPAddress.Loopback;
    }

    public static async Task<IPAddress> ResolveHostAsync(string host)
    {
        var clean = host.Trim();
        if (clean.StartsWith('[') && clean.EndsWith(']'))
            clean = clean[1..^1];

        if (IPAddress.TryParse(clean, out var ip))
            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        var addrs = await Dns.GetHostAddressesAsync(clean);
        var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (v4 != null) return v4;

        var any = addrs.FirstOrDefault();
        if (any != null) return any.IsIPv4MappedToIPv6 ? any.MapToIPv4() : any;

        throw new InvalidOperationException($"Nelze přeložit adresu '{host}'.");
    }
}
