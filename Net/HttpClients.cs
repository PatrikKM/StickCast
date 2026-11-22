using System.Net;

namespace StickCast.Net;

/// <summary>
/// Centralized shared HttpClient instances to prevent socket exhaustion
/// and keep connection pooling consistent across StickCast.Net.
/// </summary>
internal static class HttpClients
{
    private static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout, bool autoDecompress = true) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = connectTimeout,
        AllowAutoRedirect = true,
        AutomaticDecompression = autoDecompress ? DecompressionMethods.All : DecompressionMethods.None,
    };

    /// <summary>Short-timeout client for UPnP SOAP, SSDP descriptions, M3U playlists, and StickAdmin.</summary>
    public static HttpClient Standard { get; } = new(CreateHandler(TimeSpan.FromSeconds(5)))
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>Client for subtitle downloads and translation API calls.</summary>
    public static HttpClient ShortFetch { get; } = new(CreateHandler(TimeSpan.FromSeconds(8)))
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Streaming client with infinite request timeout for HTTP proxy transfers and large tool downloads.</summary>
    public static HttpClient LongTransfer { get; } = new(CreateHandler(TimeSpan.FromSeconds(10), autoDecompress: false))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
