using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StickCast.Net;

public interface ILocalMediaHost
{
    Task PlayAsync(string url, string title, string mime, string? subtitleUrl);
    Task ResumeAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan pos);
    Task<(TimeSpan Position, TimeSpan Duration)> GetPositionAsync();
    Task<string> GetTransportStateAsync();
    Task<int?> GetVolumeAsync();
    Task SetVolumeAsync(int volume);
    Task<bool?> GetMuteAsync();
    Task SetMuteAsync(bool mute);
}

public sealed class DlnaException : Exception
{
    public int? ErrorCode { get; }

    public DlnaException(string msg) : base(msg) { }

    public DlnaException(string msg, int? errorCode, Exception? inner = null) : base(msg, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>UPnP/DLNA Media Renderer (DMR) controlled through AVTransport + RenderingControl SOAP.</summary>
public sealed class DlnaRenderer
{
    private static readonly Regex BareAmpRegex =
        new(@"&(?!amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)", RegexOptions.Compiled);

    private readonly SemaphoreSlim _soapLock = new(1, 1);

    public string FriendlyName { get; private init; } = "";
    public string Manufacturer { get; private init; } = "";
    public string ModelName { get; private init; } = "";
    public string Location { get; private init; } = "";
    public string Udn { get; private init; } = "";
    public string Host => IsLocalMonitor ? "127.0.0.1" : new Uri(Location).Host;

    private Uri _avtUrl = null!;
    private string _avtType = "urn:schemas-upnp-org:service:AVTransport:1";
    private Uri? _rcUrl;
    private string _rcType = "urn:schemas-upnp-org:service:RenderingControl:1";
    private ILocalMediaHost? _localHost;

    public bool IsLocalMonitor => _localHost != null;
    public bool HasVolume => _rcUrl != null || _localHost != null;

    public override string ToString() => IsLocalMonitor ? FriendlyName : $"{FriendlyName}  ({Host})";

    public static DlnaRenderer CreateLocalMonitor(string id, string friendlyName, string modelName, ILocalMediaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new DlnaRenderer
        {
            Location = $"local://monitor/{id}",
            FriendlyName = friendlyName,
            Manufacturer = "Tento počítač",
            ModelName = modelName,
            Udn = $"uuid:local-monitor-{id}",
            _localHost = host,
        };
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>Downloads a device description; returns null if it is not a media renderer.</summary>
    public static async Task<DlnaRenderer?> FromLocationAsync(string location)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var locUri)) return null;
            var rawBytes = await HttpClients.Standard.GetByteArrayAsync(locUri);
            var xml = Encoding.UTF8.GetString(rawBytes);
            var doc = ParseXmlResilient(xml);
            if (doc?.Root == null) return null;

            var urlBase = Child(doc.Root, "URLBase");
            var baseUri = Uri.TryCreate(urlBase, UriKind.Absolute, out var parsedBase) ? parsedBase : locUri;

            var allDevices = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("device", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (allDevices.Count == 0) return null;

            var rootDevice = allDevices[0];
            XElement? matchedDevice = null;
            Uri? avt = null, rc = null;
            string? avtType = null, rcType = null;

            // Prefer a device (including nested devices) that declares MediaRenderer and owns AVTransport.
            foreach (var dev in allDevices)
            {
                var (dAvt, dAvtType, dRc, dRcType) = ExtractDirectServices(dev, baseUri);
                if (dAvt == null) continue;

                var devType = Child(dev, "deviceType") ?? "";
                bool isMediaRenderer = devType.Contains(":MediaRenderer:", StringComparison.OrdinalIgnoreCase);

                if (matchedDevice == null || isMediaRenderer)
                {
                    matchedDevice = dev;
                    avt = dAvt;
                    avtType = dAvtType;
                    rc = dRc;
                    rcType = dRcType;
                    if (isMediaRenderer) break;
                }
            }

            // Fallback for non-standard XML where services are not inside <serviceList>.
            if (avt == null)
            {
                matchedDevice = rootDevice;
                foreach (var svc in doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("service", StringComparison.OrdinalIgnoreCase)))
                {
                    AssignService(svc, baseUri, ref avt, ref avtType, ref rc, ref rcType);
                }
            }

            if (avt == null) return null;

            // If RenderingControl sits on a sibling/parent device, still pick it up.
            if (rc == null)
            {
                foreach (var svc in doc.Descendants()
                    .Where(e => e.Name.LocalName.Equals("service", StringComparison.OrdinalIgnoreCase)))
                {
                    var type = Child(svc, "serviceType") ?? "";
                    var ctrl = Child(svc, "controlURL");
                    if (type.Contains(":RenderingControl:", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(ctrl) &&
                        Uri.TryCreate(baseUri, ctrl, out var rcUri))
                    {
                        rc = rcUri;
                        rcType = type;
                        break;
                    }
                }
            }

            var infoDev = matchedDevice ?? rootDevice;
            return new DlnaRenderer
            {
                Location = location,
                FriendlyName = NonEmpty(Child(infoDev, "friendlyName"), Child(rootDevice, "friendlyName")) ?? "DLNA přijímač",
                Manufacturer = NonEmpty(Child(infoDev, "manufacturer"), Child(rootDevice, "manufacturer")) ?? "",
                ModelName = NonEmpty(Child(infoDev, "modelName"), Child(rootDevice, "modelName")) ?? "",
                Udn = NonEmpty(Child(infoDev, "UDN"), Child(rootDevice, "UDN")) ?? "",
                _avtUrl = avt,
                _avtType = avtType!,
                _rcUrl = rc,
                _rcType = rcType ?? "urn:schemas-upnp-org:service:RenderingControl:1",
            };
        }
        catch
        {
            return null;
        }
    }

    private static (Uri? Avt, string? AvtType, Uri? Rc, string? RcType) ExtractDirectServices(
        XElement deviceEl, Uri baseUri)
    {
        Uri? avt = null, rc = null;
        string? avtType = null, rcType = null;

        var serviceList = deviceEl.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals("serviceList", StringComparison.OrdinalIgnoreCase));
        if (serviceList == null) return (null, null, null, null);

        foreach (var svc in serviceList.Elements()
            .Where(e => e.Name.LocalName.Equals("service", StringComparison.OrdinalIgnoreCase)))
        {
            AssignService(svc, baseUri, ref avt, ref avtType, ref rc, ref rcType);
        }

        return (avt, avtType, rc, rcType);
    }

    private static void AssignService(XElement svc, Uri baseUri,
        ref Uri? avt, ref string? avtType, ref Uri? rc, ref string? rcType)
    {
        var type = Child(svc, "serviceType") ?? "";
        var ctrl = Child(svc, "controlURL");
        if (string.IsNullOrEmpty(ctrl) || !Uri.TryCreate(baseUri, ctrl, out var ctrlUri)) return;

        if (type.Contains(":AVTransport:", StringComparison.OrdinalIgnoreCase) && avt == null)
        {
            avt = ctrlUri;
            avtType = type;
        }
        else if (type.Contains(":RenderingControl:", StringComparison.OrdinalIgnoreCase) && rc == null)
        {
            rc = ctrlUri;
            rcType = type;
        }
    }

    private static string? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static string? NonEmpty(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : (!string.IsNullOrWhiteSpace(fallback) ? fallback : null);

    // ------------------------------------------------------------------ playback

    public async Task PlayAsync(string url, string title, string mime, bool isLocal, string? subtitleUrl = null)
    {
        if (_localHost != null)
        {
            await _localHost.PlayAsync(url, title, mime, subtitleUrl);
            return;
        }

        try { await StopAsync(); } catch { /* not playing – fine */ }

        var meta = BuildDidl(url, title, mime, isLocal, subtitleUrl);
        try
        {
            await AvtAsync("SetAVTransportURI", ("CurrentURI", url), ("CurrentURIMetaData", meta));
        }
        catch (DlnaException)
        {
            // Some cheap renderers choke on metadata – retry without it.
            await AvtAsync("SetAVTransportURI", ("CurrentURI", url), ("CurrentURIMetaData", ""));
        }
        await Task.Delay(300);
        await AvtAsync("Play", ("Speed", "1"));
    }

    public async Task<bool> TrySetNextUriAsync(string url, string title, string mime, bool isLocal, string? subtitleUrl = null)
    {
        if (_localHost != null) return false;
        try
        {
            var meta = BuildDidl(url, title, mime, isLocal, subtitleUrl);
            await AvtAsync("SetNextAVTransportURI", ("NextURI", url), ("NextURIMetaData", meta));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task ResumeAsync() => _localHost != null ? _localHost.ResumeAsync() : AvtAsync("Play", ("Speed", "1"));
    public Task PauseAsync() => _localHost != null ? _localHost.PauseAsync() : AvtAsync("Pause");
    public Task StopAsync() => _localHost != null ? _localHost.StopAsync() : AvtAsync("Stop");

    public Task SeekAsync(TimeSpan pos) =>
        _localHost != null ? _localHost.SeekAsync(pos) : AvtAsync("Seek", ("Unit", "REL_TIME"), ("Target", FormatTime(pos)));

    public async Task<(TimeSpan Position, TimeSpan Duration)> GetPositionAsync()
    {
        if (_localHost != null) return await _localHost.GetPositionAsync();
        var d = await AvtAsync("GetPositionInfo");
        return (ParseTime(Find(d, "RelTime")), ParseTime(Find(d, "TrackDuration")));
    }

    public async Task<string> GetTransportStateAsync()
    {
        if (_localHost != null) return await _localHost.GetTransportStateAsync();
        var d = await AvtAsync("GetTransportInfo");
        return Find(d, "CurrentTransportState")?.Trim() ?? "UNKNOWN";
    }

    public async Task<int?> GetVolumeAsync()
    {
        if (_localHost != null) return await _localHost.GetVolumeAsync();
        if (_rcUrl == null) return null;
        var d = await InvokeAsync(_rcUrl, _rcType, "GetVolume", ("InstanceID", "0"), ("Channel", "Master"));
        return int.TryParse(Find(d, "CurrentVolume")?.Trim(), out var v) ? v : null;
    }

    public async Task SetVolumeAsync(int volume)
    {
        if (_localHost != null)
        {
            await _localHost.SetVolumeAsync(volume);
            return;
        }
        if (_rcUrl == null) return;
        await InvokeAsync(_rcUrl, _rcType, "SetVolume", ("InstanceID", "0"), ("Channel", "Master"),
            ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture)));
    }

    public async Task<bool?> GetMuteAsync()
    {
        if (_localHost != null) return await _localHost.GetMuteAsync();
        if (_rcUrl == null) return null;
        var d = await InvokeAsync(_rcUrl, _rcType, "GetMute", ("InstanceID", "0"), ("Channel", "Master"));
        var val = Find(d, "CurrentMute")?.Trim();
        if (string.IsNullOrEmpty(val)) return null;
        if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    public async Task SetMuteAsync(bool mute)
    {
        if (_localHost != null)
        {
            await _localHost.SetMuteAsync(mute);
            return;
        }
        if (_rcUrl == null) return;
        await InvokeAsync(_rcUrl, _rcType, "SetMute", ("InstanceID", "0"), ("Channel", "Master"),
            ("DesiredMute", mute ? "1" : "0"));
    }

    // ------------------------------------------------------------------ SOAP

    private Task<XDocument> AvtAsync(string action, params (string, string)[] args)
    {
        var all = new List<(string, string)>(args.Length + 1) { ("InstanceID", "0") };
        all.AddRange(args);
        return InvokeAsync(_avtUrl, _avtType, action, all.ToArray());
    }

    private async Task<XDocument> InvokeAsync(Uri control, string serviceType, string action,
        params (string Key, string Value)[] args)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append($"<u:{action} xmlns:u=\"{SecurityElement.Escape(serviceType)}\">");
        foreach (var (k, v) in args) sb.Append($"<{k}>{SecurityElement.Escape(v)}</{k}>");
        sb.Append($"</u:{action}></s:Body></s:Envelope>");

        var payloadBytes = Encoding.UTF8.GetBytes(sb.ToString());

        await _soapLock.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, control);
                req.Headers.ExpectContinue = false;
                req.Headers.ConnectionClose = true;
                req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");

                var content = new ByteArrayContent(payloadBytes);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
                content.Headers.ContentLength = payloadBytes.Length;
                req.Content = content;

                HttpResponseMessage resp;
                string body;
                try
                {
                    resp = await HttpClients.Standard.SendAsync(req);
                    using (resp)
                    {
                        body = await resp.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex) when (attempt == 0 && (ex is HttpRequestException || ex is IOException))
                {
                    await Task.Delay(150);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    throw CreateSoapException(action, resp.StatusCode, body);

                return ParseXmlResilient(body) ?? new XDocument(new XElement("empty"));
            }

            throw new DlnaException($"{action}: spojení selhalo");
        }
        finally
        {
            _soapLock.Release();
        }
    }

    private static DlnaException CreateSoapException(string action, HttpStatusCode status, string body)
    {
        int? parsedCode = null;
        string detail = status.ToString();
        var errDoc = ParseXmlResilient(body);
        if (errDoc != null)
        {
            var codeStr = Find(errDoc, "errorCode")?.Trim();
            var desc = Find(errDoc, "errorDescription")?.Trim();
            var fault = Find(errDoc, "faultstring")?.Trim();

            if (int.TryParse(codeStr, out var code))
            {
                parsedCode = code;
                var friendly = DescribeUpnpError(code);
                var suffix = !string.IsNullOrEmpty(desc) ? desc : friendly;
                detail = string.IsNullOrEmpty(suffix)
                    ? $"UPnP chyba {code}"
                    : $"UPnP chyba {code} ({suffix})";
            }
            else if (!string.IsNullOrEmpty(fault))
            {
                detail = fault;
            }
        }
        return new DlnaException($"{action}: {detail}", parsedCode);
    }

    private static string? DescribeUpnpError(int code) => code switch
    {
        401 => "Neplatná akce",
        402 => "Neplatné argumenty",
        501 => "Akce selhala",
        602 => "Volitelná akce není implementována",
        701 => "Přechod stavu není dostupný",
        702 => "Žádný obsah",
        703 => "Chyba čtení zdroje",
        704 => "Formát není podporován",
        705 => "Transport je uzamčen",
        710 => "Režim posunu není podporován",
        711 => "Neplatná cílová pozice",
        712 => "Nepodporovaná metadata",
        714 => "Neplatný typ média (MIME)",
        716 => "Zdroj nenalezen",
        718 => "Neplatné InstanceID",
        _ => null,
    };

    private static string? Find(XDocument d, string localName) =>
        d.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    // ------------------------------------------------------------------ helpers

    internal static XDocument? ParseXmlResilient(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = TrimToXml(raw);
        if (clean.Length == 0) return null;

        try
        {
            return XDocument.Parse(clean);
        }
        catch (XmlException)
        {
            var sanitized = SanitizeBrokenXml(clean);
            try { return XDocument.Parse(sanitized); }
            catch { return null; }
        }
    }

    private static string TrimToXml(string raw)
    {
        var s = raw.Trim('\uFEFF', '\0', ' ', '\t', '\r', '\n');
        var firstLt = s.IndexOf('<');
        var lastGt = s.LastIndexOf('>');
        if (firstLt < 0 || lastGt <= firstLt) return "";
        return s[firstLt..(lastGt + 1)];
    }

    private static string SanitizeBrokenXml(string xml)
    {
        var sb = new StringBuilder(xml.Length);
        foreach (char ch in xml)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' ||
                (ch >= 0x20 && ch <= 0xD7FF) ||
                (ch >= 0xE000 && ch <= 0xFFFD))
            {
                sb.Append(ch);
            }
        }
        return BareAmpRegex.Replace(sb.ToString(), "&amp;");
    }

    internal static string BuildProtocolInfo(string mime, bool isLocal)
    {
        var m = string.IsNullOrWhiteSpace(mime) ? "video/mp4" : mime.Trim();
        if (!isLocal) return $"http-get:*:{m}:*";
        return $"http-get:*:{m}:{Mime.DlnaFeatures(m)}";
    }

    internal static string BuildDidl(string url, string title, string mime, bool isLocal, string? subtitleUrl = null)
    {
        var m = string.IsNullOrWhiteSpace(mime) ? "video/mp4" : mime.Trim();
        var upnpClass = m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "object.item.audioItem.musicTrack"
            : m.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "object.item.imageItem.photo"
            : "object.item.videoItem";
        var protocolInfo = BuildProtocolInfo(m, isLocal);

        var hasSub = !string.IsNullOrWhiteSpace(subtitleUrl);
        var secNs = hasSub ? " xmlns:sec=\"http://www.sec.co.kr/\"" : "";
        var captionEl = "";
        if (hasSub)
        {
            var sub = subtitleUrl!.Trim();
            var pathPart = sub.Split('?', '#')[0];
            var subType = pathPart.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ? "vtt" : "srt";
            captionEl = $"<sec:CaptionInfoEx sec:type=\"{subType}\">{SecurityElement.Escape(sub)}</sec:CaptionInfoEx>";
        }

        return "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
               "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
               $"xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"{secNs}>" +
               "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
               $"<dc:title>{SecurityElement.Escape(title)}</dc:title>" +
               $"<upnp:class>{upnpClass}</upnp:class>" +
               $"<res protocolInfo=\"{SecurityElement.Escape(protocolInfo)}\">{SecurityElement.Escape(url)}</res>" +
               captionEl +
               "</item></DIDL-Lite>";
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static TimeSpan ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return TimeSpan.Zero;
        var trimmed = s.Trim();
        if (trimmed.Equals("NOT_IMPLEMENTED", StringComparison.OrdinalIgnoreCase) || !trimmed.Contains(':'))
            return TimeSpan.Zero;

        var p = trimmed.Split(':');
        if (p.Length < 2 || p.Length > 3) return TimeSpan.Zero;

        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return TimeSpan.Zero;
        if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return TimeSpan.Zero;

        double sec = 0;
        if (p.Length == 3)
        {
            var secStr = p[2].Replace(',', '.');
            var slash = secStr.IndexOf('/');
            if (slash > 0) secStr = secStr[..slash];
            if (!double.TryParse(secStr, NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                return TimeSpan.Zero;
        }

        var totalSeconds = h * 3600.0 + m * 60.0 + sec;
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds <= 0)
            return TimeSpan.Zero;
        if (totalSeconds >= TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.MaxValue;

        return TimeSpan.FromSeconds(totalSeconds);
    }

    public static async Task<IPAddress> GetHostAddressAsync(DlnaRenderer r) => await NetUtil.ResolveHostAsync(r.Host);
}
