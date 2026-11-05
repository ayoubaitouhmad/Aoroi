using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Service.Dns;

/// <summary>
/// STEP 1 — a bare DNS forwarder.
///
/// What DNS looks like on the wire (the part that matters here):
///   - A client (the OS resolver, a browser) sends a small UDP packet to port 53 asking
///     "what is the address of example.com?".
///   - A DNS server reads that packet and sends a UDP packet back with the answer.
///   - The packet is a compact binary format (a 12-byte header, then the question, then
///     answers). We will learn to *read* that format in Step 2.
///
/// In this step we don't understand the packet at all — we just move bytes:
///   client → us (on 127.0.0.1:53) → upstream resolver (e.g. 1.1.1.1:53) → us → client.
///
/// That's a legitimate, complete DNS server already: a "forwarding resolver". It answers
/// nothing itself; it relays to a real resolver and relays the reply straight back. Because we
/// never touch the bytes, it transparently handles every record type (A, AAAA, MX, TXT, …) and
/// EDNS. In later steps we start peeking at the question so we can block or redirect.
///
/// Transport note: classic DNS is UDP. Large answers can fall back to TCP/53, which this step
/// does not implement — the vast majority of lookups are UDP, and we add TCP later if needed.
/// </summary>
public sealed class DnsForwarder
{
    private readonly ConfigStore _configStore;
    private readonly ILogger<DnsForwarder> _log;

    public DnsForwarder(ConfigStore configStore, ILogger<DnsForwarder> log)
    {
        _configStore = configStore;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var cfg = _configStore.Load().Dns;

        if (!cfg.Enabled)
        {
            _log.LogInformation("DNS filter disabled in config; not starting listener.");
            return;
        }

        var listenEndpoint = new IPEndPoint(IPAddress.Parse(cfg.ListenAddress), cfg.Port);

        // One UDP socket, bound to 127.0.0.1:53, receives every query for this machine.
        using var server = new UdpClient(listenEndpoint);

        _log.LogInformation("DNS forwarder listening on {Endpoint}, upstream = {Upstream}",
            listenEndpoint, string.Join(", ", cfg.UpstreamServers));

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult query;
            try
            {
                // Wait for the next incoming query datagram.
                query = await server.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break; // service stopping
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error receiving DNS query; continuing.");
                continue;
            }

            // Handle each query without blocking the receive loop, so slow upstreams don't
            // stall other lookups. Fire-and-forget is fine here: each task is short-lived.
            _ = HandleQueryAsync(server, query, cfg, ct);
        }

        _log.LogInformation("DNS forwarder stopped.");
    }

    private async Task HandleQueryAsync(
        UdpClient server, UdpReceiveResult query, DnsConfig cfg, CancellationToken ct)
    {
        try
        {
            byte[] response = await ForwardAsync(query.Buffer, cfg, ct);

            if (response.Length > 0)
            {
                // Relay the upstream's reply back to whoever asked us.
                await server.SendAsync(response, response.Length, query.RemoteEndPoint);
            }
            // If no upstream answered, we simply drop it; the client will retry/time out,
            // exactly as it would against any unreachable resolver.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to handle a DNS query from {Client}.", query.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Send the raw query bytes to each upstream resolver in turn and return the first reply.
    /// </summary>
    private static async Task<byte[]> ForwardAsync(byte[] request, DnsConfig cfg, CancellationToken ct)
    {
        foreach (var upstream in cfg.UpstreamServers)
        {
            if (!IPAddress.TryParse(upstream, out var upstreamIp))
                continue;

            try
            {
                using var client = new UdpClient();
                var upstreamEndpoint = new IPEndPoint(upstreamIp, 53);

                // Send the client's exact query bytes onward, unmodified.
                await client.SendAsync(request, request.Length, upstreamEndpoint);

                // Wait for the reply, but give up after the timeout and try the next resolver.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(cfg.UpstreamTimeoutMs);

                try
                {
                    UdpReceiveResult reply = await client.ReceiveAsync(timeoutCts.Token);
                    return reply.Buffer;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This upstream timed out — fall through to the next one.
                }
            }
            catch (Exception)
            {
                // Network error talking to this upstream — try the next one.
            }
        }

        // No upstream answered in time.
        return Array.Empty<byte>();
    }
}
