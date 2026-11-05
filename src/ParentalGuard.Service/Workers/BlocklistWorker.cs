using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentalGuard.Service.Dns;

namespace ParentalGuard.Service.Workers;

/// <summary>
/// Keeps the blocklist current. On startup it loads the cache+fallback instantly (so protection
/// is up before any network call), then downloads fresh lists, then repeats the download every
/// <c>RefreshHours</c>. A failed refresh is logged and retried next cycle — it never takes the
/// Service down.
/// </summary>
public sealed class BlocklistWorker : BackgroundService
{
    private readonly BlocklistUpdater _updater;
    private readonly ConfigStore _configStore;
    private readonly ILogger<BlocklistWorker> _log;

    public BlocklistWorker(BlocklistUpdater updater, ConfigStore configStore, ILogger<BlocklistWorker> log)
    {
        _updater = updater;
        _configStore = configStore;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Blocklist worker starting.");

        // 1) Instant protection from cache + bundled fallback, before touching the network.
        try
        {
            _updater.LoadFromCacheOrFallback();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Initial cache/fallback load failed; continuing.");
        }

        // 2) Download now, then on the configured interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _updater.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Blocklist refresh failed; will retry next cycle.");
            }

            int hours = Math.Max(1, _configStore.Load().Dns.RefreshHours);
            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("Blocklist worker exiting.");
    }
}
