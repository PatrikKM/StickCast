using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace StickCast.Net;

public sealed record StickAdminInfo(
    bool Reachable,
    int? StatusCode,
    string? Title,
    string? Server,
    string Url,
    string? Error = null,
    string? Hint = null);

/// <summary>Client for probing the stick's built-in HTTP admin web interface (e.g. 192.168.203.1:80).</summary>
public static class StickAdmin
{
    private static readonly Regex TitleRegex = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SsidHintRegex = new(@"\b(?:WifiDisplay|MiraScreen|EZMira|AnyCast|EZCast)[-_ ]?[A-Za-z0-9]{2,16}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FwHintRegex = new(@"\b(?:firmware|fw|sw_ver|ver(?:sion)?)\s*[:=]\s*[""']?([0-9]+(?:\.[0-9A-Za-z_-]+)*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<StickAdminInfo> ProbeAsync(string? host, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(host) ? "192.168.203.1" : host.Trim();
        var url = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? target
            : $"http://{target}/";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.ConnectionClose = true;

            using var resp = await HttpClients.Standard.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var server = resp.Headers.Server.ToString();
            if (string.IsNullOrWhiteSpace(server)) server = null;

            string? title = null;
            string? hint = null;
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (contentType == null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
                var buf = new byte[65536];
                int total = 0;
                while (total < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), timeoutCts.Token);
                    if (n <= 0) break;
                    total += n;
                }
                var html = Encoding.UTF8.GetString(buf, 0, total);
                var m = TitleRegex.Match(html);
                if (m.Success)
                {
                    var decoded = WebUtility.HtmlDecode(m.Groups[1].Value);
                    var collapsed = WhitespaceRegex.Replace(decoded, " ").Trim();
                    if (!string.IsNullOrEmpty(collapsed)) title = collapsed;
                }
                hint = ExtractHint(html);
            }

            return new StickAdminInfo(true, (int)resp.StatusCode, title, server, url, Hint: hint);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new StickAdminInfo(false, null, null, null, url, "Časový limit vypršel");
        }
        catch (Exception ex)
        {
            return new StickAdminInfo(false, null, null, null, url, ex.Message);
        }
    }

    internal static string? ExtractHint(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var bodyOnly = TitleRegex.Replace(html, " ");
        var parts = new List<string>();
        var ssidMatches = SsidHintRegex.Matches(bodyOnly);
        string? bestSsid = null;
        foreach (Match m in ssidMatches)
        {
            var val = WhitespaceRegex.Replace(m.Value, " ").Trim();
            if (string.IsNullOrEmpty(val)) continue;
            if (val.Any(char.IsDigit)) { bestSsid = val; break; }
            bestSsid ??= val;
        }
        if (!string.IsNullOrEmpty(bestSsid)) parts.Add(bestSsid);

        var fwMatch = FwHintRegex.Match(html);
        if (fwMatch.Success)
        {
            var fw = fwMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(fw)) parts.Add($"FW {fw}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : null;
    }
}
