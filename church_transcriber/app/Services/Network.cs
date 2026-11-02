using System.Net;
using System.Net.Sockets;

namespace ChurchTranscriber.Services;

public sealed record NetCheck(string Host, bool Ok, string? Error, double Ms);

/// <summary>Recognises temporary network failures and checks DNS from inside the add-on.</summary>
public static class Network
{
    private static readonly string[] Hosts = { "www.youtube.com", "huggingface.co" };

    private static readonly string[] DnsMarkers = { "name resolution", "failed to resolve", "name or service not known", "nodename nor servname", "getaddrinfo" };
    private static readonly string[] TransientMarkers =
    {
        "timed out", "timeout", "connection reset", "connection aborted", "remote end closed", "network is unreachable",
        "unable to connect", "connection refused", "eof occurred", "incompleteread", "temporarily unavailable",
        "http error 500", "http error 502", "http error 503", "http error 504", "didn't respond within",
        "couldn't download the whisper model", "localentrynotfound", "connectionerror"
    };

    private static string Text(Exception ex) => ((ex.Message + "\n" + (ex as ToolException)?.Details) ?? "").ToLowerInvariant();

    public static bool IsDns(Exception ex) => DnsMarkers.Any(Text(ex).Contains);

    public static bool IsTransient(Exception ex)
    {
        var t = Text(ex);
        if (t.Contains("not a bot") || t.Contains("private video") || t.Contains("members-only") || t.Contains("unavailable")) return false;
        return IsDns(ex) || TransientMarkers.Any(t.Contains);
    }

    public static string Describe(Exception ex) =>
        IsDns(ex) ? "Home Assistant couldn't look up YouTube's address (DNS)" : "Couldn't reach YouTube (network problem)";

    public static async Task<List<NetCheck>> CheckAsync(CancellationToken ct)
    {
        var results = new List<NetCheck>();
        foreach (var host in Hosts)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
                results.Add(addrs.Length > 0 ? new NetCheck(host, true, null, sw.Elapsed.TotalMilliseconds)
                                              : new NetCheck(host, false, "no addresses returned", sw.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { results.Add(new NetCheck(host, false, "lookup timed out", sw.Elapsed.TotalMilliseconds)); }
            catch (SocketException ex) { results.Add(new NetCheck(host, false, ex.Message, sw.Elapsed.TotalMilliseconds)); }
        }
        return results;
    }
}
