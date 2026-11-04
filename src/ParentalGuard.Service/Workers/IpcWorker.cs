using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentalGuard.Service.Ipc;

namespace ParentalGuard.Service.Workers;

/// <summary>
/// Hosts the <see cref="PipeServer"/> as a background service for the Generic Host.
/// Keeping the pipe loop in a BackgroundService means the Windows Service lifecycle
/// (start/stop/shutdown) drives it cleanly through the cancellation token.
/// </summary>
public sealed class IpcWorker : BackgroundService
{
    private readonly PipeServer _server;
    private readonly ILogger<IpcWorker> _log;

    public IpcWorker(PipeServer server, ILogger<IpcWorker> log)
    {
        _server = server;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ParentalGuard service worker started");
        try
        {
            await _server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
        _log.LogInformation("ParentalGuard service worker exiting");
    }
}
