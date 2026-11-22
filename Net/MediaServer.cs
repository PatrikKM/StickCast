using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StickCast.Net;

/// <summary>
/// Tiny HTTP/1.1 file server (with Range &amp; Keep-Alive support for seeking) that lets the dongle pull local files from the PC.
/// Uses TcpListener so it needs no admin rights / URL ACL.
/// </summary>
public sealed class MediaServer : IDisposable
{
    private const int MaxHeaderBytes = 16384;
    private const int StreamBufferSize = 65536;
    private const string ProxyUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private TcpListener? _listener;
    private readonly LruBiMap<string> _files;
    private readonly LruBiMap<ProxyEntry> _proxies;
    private readonly LruMap<string, string> _mediaSubtitles;
    private readonly ConcurrentDictionary<string, LiveStreamEntry> _liveStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TcpClient, byte> _activeClients = new();
    private readonly SemaphoreSlim _connLimiter;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    private sealed record LiveStreamEntry(string ContentType, Func<Stream, CancellationToken, Task> Writer);
    private sealed record ProxyEntry(string RemoteUrl, string ContentType);

    private sealed class LruMap<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

        public LruMap(int capacity, IEqualityComparer<TKey> comparer)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(comparer);
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    existing.Value = (key, value);
                    _order.AddFirst(existing);
                    return;
                }

                var node = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
                _order.AddFirst(node);
                _map[key] = node;

                while (_map.Count > _capacity && _order.Last is { } oldest)
                {
                    _order.RemoveLast();
                    _map.Remove(oldest.Value.Key);
                }
            }
        }

        public void Remove(TKey key)
        {
            lock (_lock)
            {
                if (_map.Remove(key, out var node))
                    _order.Remove(node);
            }
        }
    }

    private sealed class LruBiMap<TValue>
    {
        private readonly int _capacity;
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<(string Id, string LookupKey, TValue Value)>> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lookupToId;
        private readonly LinkedList<(string Id, string LookupKey, TValue Value)> _order = new();

        public LruBiMap(int capacity, IEqualityComparer<string> lookupComparer)
        {
            _capacity = Math.Max(1, capacity);
            _lookupToId = new Dictionary<string, string>(lookupComparer);
        }

        public string GetOrAdd(string lookupKey, Func<TValue> factory)
        {
            lock (_lock)
            {
                if (_lookupToId.TryGetValue(lookupKey, out var existingId) && _byId.TryGetValue(existingId, out var existingNode))
                {
                    _order.Remove(existingNode);
                    _order.AddFirst(existingNode);
                    return existingId;
                }

                var id = Guid.NewGuid().ToString("N")[..10];
                var node = new LinkedListNode<(string Id, string LookupKey, TValue Value)>((id, lookupKey, factory()));
                _order.AddFirst(node);
                _byId[id] = node;
                _lookupToId[lookupKey] = id;

                while (_byId.Count > _capacity && _order.Last is { } oldest)
                {
                    _order.RemoveLast();
                    _byId.Remove(oldest.Value.Id);
                    _lookupToId.Remove(oldest.Value.LookupKey);
                }
                return id;
            }
        }

        public bool TryGetById(string id, out TValue value)
        {
            lock (_lock)
            {
                if (_byId.TryGetValue(id, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default!;
            return false;
        }
    }

    public MediaServer(int maxConcurrentConnections = 64, int maxFileEntries = 512, int maxProxyEntries = 200)
    {
        if (maxConcurrentConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentConnections));
        MaxConcurrentConnections = maxConcurrentConnections;
        _connLimiter = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
        _files = new LruBiMap<string>(maxFileEntries, StringComparer.OrdinalIgnoreCase);
        _proxies = new LruBiMap<ProxyEntry>(maxProxyEntries, StringComparer.Ordinal);
        _mediaSubtitles = new LruMap<string, string>(Math.Max(maxFileEntries, maxProxyEntries), StringComparer.Ordinal);
    }

    public int Port { get; private set; }
    public int MaxConcurrentConnections { get; }
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public Action<string>? Log { get; set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener != null) return;

        for (int port = 8765; port < 8800; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                _listener = l;
                Port = port;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null)
            throw new InvalidOperationException("Nepodařilo se otevřít port pro mediální server.");

        _ = AcceptLoopAsync(_cts.Token);
    }

    public string Register(string path)
    {
        string normalized;
        try { normalized = Path.GetFullPath(path); }
        catch { normalized = path; }

        return _files.GetOrAdd(normalized, () => normalized);
    }

    public string GetUrl(string path, IPAddress localIp)
    {
        var id = Register(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var host = localIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{localIp}]" : localIp.ToString();
        // ASCII-only file name in URL – some dongles fail on encoded Unicode.
        return $"http://{host}:{Port}/m/{id}/media{ext}";
    }

    public string RegisterLiveStream(string id, string contentType, Func<Stream, CancellationToken, Task> writer)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Neplatné ID živého streamu.", nameof(id));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content-Type nesmí být prázdný.", nameof(contentType));
        ArgumentNullException.ThrowIfNull(writer);

        _liveStreams[id] = new LiveStreamEntry(contentType, writer);
        return id;
    }

    public bool UnregisterLiveStream(string id) =>
        !string.IsNullOrEmpty(id) && _liveStreams.TryRemove(id, out _);

    public string GetLiveUrl(string id, IPAddress localIp)
    {
        var host = localIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{localIp}]" : localIp.ToString();
        return $"http://{host}:{Port}/live/{Uri.EscapeDataString(id)}";
    }

    public string RegisterProxy(string remoteUrl, string contentType = "video/mp4")
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("Vzdálená URL nesmí být prázdná.", nameof(remoteUrl));
        var mime = string.IsNullOrWhiteSpace(contentType) ? "video/mp4" : contentType.Trim();
        return _proxies.GetOrAdd(remoteUrl, () => new ProxyEntry(remoteUrl, mime));
    }

    public string GetProxyUrl(string remoteUrl, IPAddress localIp, string contentType = "video/mp4", string ext = ".mp4")
    {
        var id = RegisterProxy(remoteUrl, contentType);
        var host = localIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{localIp}]" : localIp.ToString();
        if (string.IsNullOrWhiteSpace(ext) || !ext.StartsWith('.')) ext = ".mp4";
        return $"http://{host}:{Port}/p/{id}/media{ext}";
    }

    public void RegisterSubtitleForMedia(string mediaUrl, string? subtitleUrl)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri)) return;
        var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length < 2) return;
        var key = $"{seg[0]}/{seg[1]}";
        if (string.IsNullOrWhiteSpace(subtitleUrl))
            _mediaSubtitles.Remove(key);
        else
            _mediaSubtitles.Set(key, subtitleUrl);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch { continue; }

            if (!_connLimiter.Wait(0))
            {
                _ = RejectBusyAsync(client);
                continue;
            }

            _activeClients[client] = 0;
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private static async Task RejectBusyAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var ns = client.GetStream();
                await WriteAsciiAsync(ns,
                    "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    CancellationToken.None);
            }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var ns = client.GetStream();
                var reader = new HeaderReader(ns);

                while (!serverCt.IsCancellationRequested)
                {
                    var (header, tooLarge) = await reader.ReadHeaderAsync(IdleTimeout, serverCt);
                    if (tooLarge)
                    {
                        await WriteSimpleResponseAsync(ns, "431 Request Header Fields Too Large", keepAlive: false, serverCt);
                        break;
                    }
                    if (header == null) break;

                    var keepAlive = await ProcessRequestAsync(client, ns, header, serverCt);
                    if (!keepAlive) break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* client closed connection (seek / stop) */ }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log?.Invoke($"HTTP chyba: {ex.Message}"); }
        finally
        {
            _activeClients.TryRemove(client, out _);
            try { _connLimiter.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> ProcessRequestAsync(TcpClient client, NetworkStream ns, string header, CancellationToken ct)
    {
        var lines = header.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            await WriteSimpleResponseAsync(ns, "400 Bad Request", keepAlive: false, ct);
            return false;
        }

        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2)
        {
            await WriteSimpleResponseAsync(ns, "400 Bad Request", keepAlive: false, ct);
            return false;
        }

        var method = first[0].ToUpperInvariant();
        var rawTarget = first[1];
        var version = first.Length >= 3 ? first[2].ToUpperInvariant() : "HTTP/1.0";

        string? rangeHeader = null;
        string? connectionHeader = null;
        string? reqTransferMode = null;

        for (int idx = 1; idx < lines.Length; idx++)
        {
            var line = lines[idx];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase))
                rangeHeader = value;
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                connectionHeader = value;
            else if (name.Equals("transferMode.dlna.org", StringComparison.OrdinalIgnoreCase))
                reqTransferMode = value;
            // getcontentFeatures.dlna.org, getcaptioninfo.sec, getMediaInfo.sec, etc. are handled safely.
        }

        bool keepAlive = IsKeepAlive(version, connectionHeader);

        if (method != "GET" && method != "HEAD")
        {
            // Close on unsupported methods in case the client sent a request body.
            await WriteAsciiAsync(ns,
                "HTTP/1.1 405 Method Not Allowed\r\nAllow: GET, HEAD\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                ct);
            return false;
        }

        if (TryResolveLiveStream(rawTarget, out var liveId, out var liveEntry))
        {
            await HandleLiveStreamAsync(client, ns, method, liveId!, liveEntry!, reqTransferMode, ct);
            return false;
        }

        if (TryResolveProxy(rawTarget, out var proxyId, out var proxyEntry))
        {
            return await HandleProxyAsync(client, ns, method, proxyId!, proxyEntry!, rangeHeader, reqTransferMode, keepAlive, ct);
        }

        var resolved = ResolveRegisteredFile(rawTarget);
        if (resolved is not { FilePath: var file, Id: var fileId })
        {
            await WriteSimpleResponseAsync(ns, "404 Not Found", keepAlive, ct);
            return keepAlive;
        }

        long len;
        FileStream? fs = null;
        try
        {
            if (method == "GET")
            {
                fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    StreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                len = fs.Length;
            }
            else
            {
                len = new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (fs != null) await fs.DisposeAsync();
            Log?.Invoke($"HTTP nelze otevřít {Path.GetFileName(file)}: {ex.Message}");
            await WriteSimpleResponseAsync(ns, "500 Internal Server Error", keepAlive: false, ct);
            return false;
        }

        try
        {
            var parsedRange = ParseRange(rangeHeader, len);
            if (parsedRange.Kind == RangeKind.Invalid)
            {
                var sb416 = new StringBuilder();
                sb416.Append("HTTP/1.1 416 Range Not Satisfiable\r\n");
                sb416.Append($"Content-Range: bytes */{len}\r\n");
                sb416.Append("Accept-Ranges: bytes\r\n");
                sb416.Append("Content-Length: 0\r\n");
                AppendConnectionHeader(sb416, keepAlive);
                sb416.Append("\r\n");
                await WriteAsciiAsync(ns, sb416.ToString(), ct);
                return keepAlive;
            }

            var mime = Mime.FromPath(file);
            var transferMode = ResolveTransferMode(reqTransferMode, mime);
            var dlnaFeatures = Mime.DlnaFeatures(mime);

            if (parsedRange.Kind == RangeKind.Multiple)
            {
                await SendMultipartAsync(client, ns, method, file, fs, len, parsedRange.Ranges!, mime, transferMode, dlnaFeatures, keepAlive, ct);
                return keepAlive;
            }

            bool partial = parsedRange.Kind == RangeKind.Single;
            long start = partial ? parsedRange.Start : 0;
            long end = partial ? parsedRange.End : Math.Max(0, len - 1);
            long contentLength = len == 0 ? 0 : (end - start + 1);

            var sb = new StringBuilder();
            sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Server: StickCast/1.0 UPnP/1.0 DLNADOC/1.50\r\n");
            sb.Append($"Content-Type: {mime}\r\n");
            sb.Append($"Content-Length: {contentLength}\r\n");
            if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{len}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append($"transferMode.dlna.org: {transferMode}\r\n");
            sb.Append($"contentFeatures.dlna.org: {dlnaFeatures}\r\n");
            if (_mediaSubtitles.TryGet("m/" + fileId, out var subHdr))
                sb.Append($"CaptionInfo.sec: {subHdr}\r\n");
            AppendConnectionHeader(sb, keepAlive);
            sb.Append("\r\n");

            await WriteAsciiAsync(ns, sb.ToString(), ct);

            Log?.Invoke($"HTTP {method} {Path.GetFileName(file)} bytes {(len == 0 ? "0-0" : $"{start}-{end}")} → {client.Client.RemoteEndPoint}");
            if (method == "HEAD" || contentLength == 0 || fs == null)
                return keepAlive;

            await StreamRangeAsync(fs, ns, start, contentLength, ct);
            return keepAlive;
        }
        finally
        {
            if (fs != null) await fs.DisposeAsync();
        }
    }

    private async Task SendMultipartAsync(
        TcpClient client,
        NetworkStream ns,
        string method,
        string file,
        FileStream? fs,
        long totalLength,
        List<(long Start, long End)> ranges,
        string mime,
        string transferMode,
        string dlnaFeatures,
        bool keepAlive,
        CancellationToken ct)
    {
        var boundary = "stickcast_" + Guid.NewGuid().ToString("N")[..12];
        var partHeaders = new List<byte[]>(ranges.Count);
        long bodyLength = 0;

        foreach (var (s, e) in ranges)
        {
            var ph = Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: {mime}\r\nContent-Range: bytes {s}-{e}/{totalLength}\r\n\r\n");
            partHeaders.Add(ph);
            bodyLength += ph.Length + (e - s + 1) + 2; // +2 for trailing \r\n
        }
        var closing = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
        bodyLength += closing.Length;

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 206 Partial Content\r\n");
        sb.Append("Server: StickCast/1.0 UPnP/1.0 DLNADOC/1.50\r\n");
        sb.Append($"Content-Type: multipart/byteranges; boundary={boundary}\r\n");
        sb.Append($"Content-Length: {bodyLength}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        sb.Append($"transferMode.dlna.org: {transferMode}\r\n");
        sb.Append($"contentFeatures.dlna.org: {dlnaFeatures}\r\n");
        AppendConnectionHeader(sb, keepAlive);
        sb.Append("\r\n");

        await WriteAsciiAsync(ns, sb.ToString(), ct);
        Log?.Invoke($"HTTP {method} {Path.GetFileName(file)} multipart ({ranges.Count} ranges) → {client.Client.RemoteEndPoint}");

        if (method == "HEAD" || fs == null) return;

        var crlf = "\r\n"u8.ToArray();
        for (int i = 0; i < ranges.Count; i++)
        {
            await WriteWithTimeoutAsync(ns, partHeaders[i], ct);
            var (s, e) = ranges[i];
            await StreamRangeAsync(fs, ns, s, e - s + 1, ct);
            await WriteWithTimeoutAsync(ns, crlf, ct);
        }
        await WriteWithTimeoutAsync(ns, closing, ct);
    }

    private static async Task StreamRangeAsync(FileStream fs, NetworkStream ns, long start, long count, CancellationToken ct)
    {
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[StreamBufferSize];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buf.Length, remaining);
            int n = await fs.ReadAsync(buf.AsMemory(0, toRead), ct);
            if (n <= 0) break;
            await WriteWithTimeoutAsync(ns, buf.AsMemory(0, n), ct);
            remaining -= n;
        }
    }

    private static async Task WriteWithTimeoutAsync(NetworkStream ns, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(WriteTimeout);
        await ns.WriteAsync(data, writeCts.Token);
    }

    private static Task WriteAsciiAsync(NetworkStream ns, string text, CancellationToken ct) =>
        WriteWithTimeoutAsync(ns, Encoding.ASCII.GetBytes(text), ct);

    private static Task WriteSimpleResponseAsync(NetworkStream ns, string status, bool keepAlive, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append("Content-Length: 0\r\n");
        AppendConnectionHeader(sb, keepAlive);
        sb.Append("\r\n");
        return WriteAsciiAsync(ns, sb.ToString(), ct);
    }

    private static void AppendConnectionHeader(StringBuilder sb, bool keepAlive)
    {
        if (keepAlive)
            sb.Append("Connection: keep-alive\r\nKeep-Alive: timeout=15\r\n");
        else
            sb.Append("Connection: close\r\n");
    }

    private static bool IsKeepAlive(string version, string? connectionHeader)
    {
        if (!string.IsNullOrEmpty(connectionHeader))
        {
            var tokens = connectionHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Any(t => t.Equals("close", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (tokens.Any(t => t.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return version == "HTTP/1.1";
    }

    private static string ResolveTransferMode(string? requested, string mime)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (requested.Equals("Streaming", StringComparison.OrdinalIgnoreCase)) return "Streaming";
            if (requested.Equals("Interactive", StringComparison.OrdinalIgnoreCase)) return "Interactive";
            if (requested.Equals("Background", StringComparison.OrdinalIgnoreCase)) return "Background";
        }
        return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Interactive" : "Streaming";
    }

    private static string[]? ExtractRouteSegments(string rawTarget)
    {
        string path = rawTarget;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return null;
            path = uri.AbsolutePath;
        }

        int q = path.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) path = path[..q];

        if (path.Contains('\\') || path.Contains("..", StringComparison.Ordinal))
            return null;

        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length >= 2 ? seg : null;
    }

    private (string Id, string FilePath)? ResolveRegisteredFile(string rawTarget)
    {
        var seg = ExtractRouteSegments(rawTarget);
        if (seg == null || seg[0] != "m") return null;

        if (!_files.TryGetById(seg[1], out var file) || !File.Exists(file))
            return null;

        return (seg[1], file);
    }

    private bool TryResolveLiveStream(string rawTarget, out string? id, out LiveStreamEntry? entry)
    {
        id = null;
        entry = null;
        var seg = ExtractRouteSegments(rawTarget);
        if (seg != null && seg[0] == "live" && _liveStreams.TryGetValue(seg[1], out entry))
        {
            id = seg[1];
            return true;
        }
        return false;
    }

    private async Task HandleLiveStreamAsync(
        TcpClient client,
        NetworkStream ns,
        string method,
        string liveId,
        LiveStreamEntry entry,
        string? reqTransferMode,
        CancellationToken ct)
    {
        var transferMode = ResolveTransferMode(reqTransferMode, entry.ContentType);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("Server: StickCast/1.0 UPnP/1.0 DLNADOC/1.50\r\n");
        sb.Append($"Content-Type: {entry.ContentType}\r\n");
        sb.Append("Accept-Ranges: none\r\n");
        sb.Append("Cache-Control: no-cache, no-store\r\n");
        sb.Append($"transferMode.dlna.org: {transferMode}\r\n");
        sb.Append("contentFeatures.dlna.org: DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01100000000000000000000000000000\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await WriteAsciiAsync(ns, sb.ToString(), ct);
        Log?.Invoke($"HTTP {method} /live/{liveId} → {client.Client.RemoteEndPoint}");
        if (method == "HEAD") return;

        await entry.Writer(ns, ct);
    }

    private bool TryResolveProxy(string rawTarget, out string? id, out ProxyEntry? entry)
    {
        id = null;
        entry = null;
        var seg = ExtractRouteSegments(rawTarget);
        if (seg != null && seg[0] == "p" && _proxies.TryGetById(seg[1], out entry))
        {
            id = seg[1];
            return true;
        }
        return false;
    }

    private async Task<bool> HandleProxyAsync(
        TcpClient client,
        NetworkStream ns,
        string method,
        string proxyId,
        ProxyEntry entry,
        string? rangeHeader,
        string? reqTransferMode,
        bool keepAlive,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, entry.RemoteUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ProxyUserAgent);
        if (entry.RemoteUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
            entry.RemoteUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        bool clientRequestedRange = !string.IsNullOrWhiteSpace(rangeHeader) &&
                                    rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);

        if (clientRequestedRange)
            req.Headers.TryAddWithoutValidation("Range", rangeHeader);
        else if (method == "HEAD")
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-");

        HttpResponseMessage resp;
        try
        {
            resp = await HttpClients.LongTransfer.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"HTTP proxy selhalo ({proxyId}): {ex.Message}");
            await WriteSimpleResponseAsync(ns, "502 Bad Gateway", keepAlive: false, ct);
            return false;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                Log?.Invoke($"HTTP proxy upstream vrátil {(int)resp.StatusCode} ({proxyId})");
                await WriteSimpleResponseAsync(ns, $"{(int)resp.StatusCode} {resp.ReasonPhrase}", keepAlive: false, ct);
                return false;
            }

            var mime = entry.ContentType;
            var transferMode = ResolveTransferMode(reqTransferMode, mime);
            var dlnaFeatures = Mime.DlnaFeatures(mime);

            long? contentLen = resp.Content.Headers.ContentLength;
            var contentRange = resp.Content.Headers.ContentRange;
            bool sendPartial = clientRequestedRange && resp.StatusCode == HttpStatusCode.PartialContent;

            if (!clientRequestedRange && contentRange?.Length is long totalLen && totalLen > 0)
                contentLen = totalLen;

            var sb = new StringBuilder();
            sb.Append(sendPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Server: StickCast/1.0 UPnP/1.0 DLNADOC/1.50\r\n");
            sb.Append($"Content-Type: {mime}\r\n");
            if (contentLen is long cl && cl >= 0)
                sb.Append($"Content-Length: {cl}\r\n");
            if (sendPartial && contentRange != null)
                sb.Append($"Content-Range: {contentRange}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append($"transferMode.dlna.org: {transferMode}\r\n");
            sb.Append($"contentFeatures.dlna.org: {dlnaFeatures}\r\n");
            if (_mediaSubtitles.TryGet("p/" + proxyId, out var subHdr))
                sb.Append($"CaptionInfo.sec: {subHdr}\r\n");
            AppendConnectionHeader(sb, method == "HEAD" && keepAlive);
            sb.Append("\r\n");

            await WriteAsciiAsync(ns, sb.ToString(), ct);
            Log?.Invoke($"HTTP proxy {method} /p/{proxyId} {(sendPartial ? rangeHeader : "full")} → {client.Client.RemoteEndPoint}");

            if (method == "HEAD")
                return keepAlive;

            await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[StreamBufferSize];
            while (true)
            {
                int n = await upstream.ReadAsync(buf, ct);
                if (n <= 0) break;
                await WriteWithTimeoutAsync(ns, buf.AsMemory(0, n), ct);
            }
            return false;
        }
    }

    private enum RangeKind { None, Single, Multiple, Invalid }

    private readonly record struct RangeParseResult(
        RangeKind Kind,
        long Start,
        long End,
        List<(long Start, long End)>? Ranges);

    private static RangeParseResult ParseRange(string? rangeHeader, long fileLength)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return new(RangeKind.None, 0, 0, null);

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return new(RangeKind.None, 0, 0, null);

        if (fileLength <= 0)
            return new(RangeKind.Invalid, 0, 0, null);

        var spec = rangeHeader[6..].Trim();
        if (spec.Length == 0)
            return new(RangeKind.Invalid, 0, 0, null);

        var parts = spec.Split(',');
        var validRanges = new List<(long Start, long End)>(parts.Length);

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            int dash = part.IndexOf('-');
            if (dash < 0 || part.IndexOf('-', dash + 1) >= 0)
                return new(RangeKind.Invalid, 0, 0, null);

            var left = part[..dash].Trim();
            var right = part[(dash + 1)..].Trim();

            if (left.Length == 0 && right.Length == 0)
                return new(RangeKind.Invalid, 0, 0, null);

            if (left.Length == 0)
            {
                if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                    return new(RangeKind.Invalid, 0, 0, null);
                long start = Math.Max(0, fileLength - suffix);
                validRanges.Add((start, fileLength - 1));
            }
            else if (right.Length == 0)
            {
                if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 0)
                    return new(RangeKind.Invalid, 0, 0, null);
                if (start < fileLength)
                    validRanges.Add((start, fileLength - 1));
            }
            else
            {
                if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                    !long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                    return new(RangeKind.Invalid, 0, 0, null);
                if (start < 0 || end < start)
                    return new(RangeKind.Invalid, 0, 0, null);
                if (start < fileLength)
                    validRanges.Add((start, Math.Min(end, fileLength - 1)));
            }
        }

        if (validRanges.Count == 0)
            return new(RangeKind.Invalid, 0, 0, null);
        if (validRanges.Count == 1)
            return new(RangeKind.Single, validRanges[0].Start, validRanges[0].End, null);
        return new(RangeKind.Multiple, 0, 0, validRanges);
    }

    private sealed class HeaderReader
    {
        private readonly NetworkStream _ns;
        private readonly byte[] _buf = new byte[MaxHeaderBytes];
        private int _start;
        private int _end;

        public HeaderReader(NetworkStream ns) => _ns = ns;

        public async Task<(string? Header, bool TooLarge)> ReadHeaderAsync(TimeSpan idleTimeout, CancellationToken ct)
        {
            while (true)
            {
                if (TryExtractHeader(out var header))
                    return (header, false);

                if (_start > 0)
                {
                    int remaining = _end - _start;
                    if (remaining > 0)
                        Buffer.BlockCopy(_buf, _start, _buf, 0, remaining);
                    _start = 0;
                    _end = remaining;
                }

                if (_end >= _buf.Length)
                    return (null, true);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(idleTimeout);

                int n;
                try
                {
                    n = await _ns.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end), timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (null, false); // idle timeout
                }

                if (n <= 0) return (null, false);
                _end += n;
            }
        }

        private bool TryExtractHeader(out string? header)
        {
            if (_end > _start)
            {
                var span = _buf.AsSpan(_start, _end - _start);
                int idx = span.IndexOf("\r\n\r\n"u8);
                if (idx >= 0)
                {
                    header = Encoding.ASCII.GetString(span[..idx]);
                    _start += idx + 4;
                    return true;
                }
            }
            header = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        foreach (var client in _activeClients.Keys)
        {
            try { client.Close(); } catch { }
        }
        _activeClients.Clear();

        _cts.Dispose();
    }
}
