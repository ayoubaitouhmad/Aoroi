using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentalGuard.Service.Dns;

namespace ParentalGuard.Service.Workers;

/// <summary>
/// Hosts the <see cref="DnsForwarder"/> as a background service, exactly like
/// <see cref="IpcWorker"/> hosts the pipe server. Both run side by side inside the one Service
/// process: the IPC worker handles the Agent, the DNS worker handles lookups.
/// </summary>
public sealed class DnsWorker : BackgroundService
{
    private readonly DnsForwarder _forwarder;
    private readonly ILogger<DnsWorker> _log;

    public DnsWorker(DnsForwarder forwarder, ILogger<DnsWorker> log)
    {
        _forwarder = forwarder;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DNS worker starting.");
        try
        {
            await _forwarder.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
        catch (Exception ex)
        {
            // A crash here must not take down the rest of the Service (counting/enforcement).
            _log.LogError(ex, "DNS worker crashed. DNS filtering is offline; other features continue.");
        }
        _log.LogInformation("DNS worker exiting.");
    }
}
