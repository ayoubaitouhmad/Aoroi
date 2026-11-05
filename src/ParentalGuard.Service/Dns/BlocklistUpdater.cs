using Microsoft.Extensions.Logging;

namespace ParentalGuard.Service.Dns;

/// <summary>
/// STEP 4 — fills the <see cref="BlocklistStore"/> with real category lists, using the
/// "download + bundled fallback" strategy:
///
///   1. <see cref="LoadFromCacheOrFallback"/> runs first, at startup, with no network: it loads
///      the bundled fallback plus any previously-cached lists from disk, so there is instant
///      protection the moment the Service starts.
///   2. <see cref="RefreshAsync"/> then downloads each enabled category, parses it, caches it to
///      disk, and rebuilds the live set. It runs at startup (after the cache load) and again on
///      the refresh timer. If a download fails, that category falls back to its cached copy, and
///      the bundled fallback is always included — so filtering never drops to nothing.
///
/// Source lists come in two shapes and we accept both:
///   - hosts format:  "0.0.0.0 badsite.com"  (take the last token)
///   - plain domains: "badsite.com"
/// with "#", "!" and ";" comments, inline "# ..." comments, and "*."/"." wildcard prefixes
/// all handled.
/// </summary>
public sealed class BlocklistUpdater
{
    // One shared HttpClient for the process. 60s is generous for a multi-MB list.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ConfigStore _configStore;
    private readonly BlocklistStore _store;
    private readonly ILogger<BlocklistUpdater> _log;

    public BlocklistUpdater(ConfigStore configStore, BlocklistStore store, ILogger<BlocklistUpdater> log)
    {
        _configStore = configStore;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Instant, offline load: bundled fallback + any cached category files already on disk.
    /// </summary>
    public void LoadFromCacheOrFallback()
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in BlocklistStore.BundledFallback)
            domains.Add(d);

        int fromCache = 0;
        if (Directory.Exists(ServicePaths.BlocklistCacheDir))
        {
            foreach (var file in Directory.EnumerateFiles(ServicePaths.BlocklistCacheDir, "*.txt"))
            {
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var dom = ParseLine(line);
                        if (dom is not null && domains.Add(dom))
                            fromCache++;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not read cached list {File}: {Msg}", file, ex.Message);
                }
            }
        }

        _store.Replace(domains);
        _log.LogInformation("Blocklist ready from cache+fallback: {Total} domains ({Cache} cached).",
            _store.Count, fromCache);
    }

    /// <summary>
    /// Download every enabled category, parse + cache each, then rebuild the live blocklist.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var cfg = _configStore.Load().Dns;

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in BlocklistStore.BundledFallback)
            all.Add(d);

        Directory.CreateDirectory(ServicePaths.BlocklistCacheDir);

        int liveCategories = 0;

        foreach (var category in cfg.Categories)
        {
            if (!category.Enabled)
                continue;

            var categoryDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in category.Sources)
            {
                try
                {
                    string text = await Http.GetStringAsync(url, ct);
                    int before = categoryDomains.Count;

                    foreach (var line in text.Split('\n'))
                    {
                        var dom = ParseLine(line);
                        if (dom is not null)
                            categoryDomains.Add(dom);
                    }

                    _log.LogInformation("Downloaded {Category}: +{Added} from {Url}",
                        category.Name, categoryDomains.Count - before, url);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Download failed for {Category} ({Url}): {Msg}",
                        category.Name, url, ex.Message);
                }
            }

            string cacheFile = Path.Combine(ServicePaths.BlocklistCacheDir, category.Name + ".txt");

            if (categoryDomains.Count > 0)
            {
                // Success: cache to disk and merge into the live set.
                try
                {
                    await File.WriteAllLinesAsync(cacheFile, categoryDomains, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not cache {Category}: {Msg}", category.Name, ex.Message);
                }

                foreach (var d in categoryDomains)
                    all.Add(d);
                liveCategories++;
            }
            else if (File.Exists(cacheFile))
            {
                // Total download failure for this category: fall back to the last cached copy.
                try
                {
                    foreach (var line in await File.ReadAllLinesAsync(cacheFile, ct))
                    {
                        var dom = ParseLine(line);
                        if (dom is not null)
                            all.Add(dom);
                    }
                    _log.LogInformation("Using cached copy for {Category}.", category.Name);
                    liveCategories++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not read cache for {Category}: {Msg}", category.Name, ex.Message);
                }
            }
            else
            {
                _log.LogWarning("No data for {Category} (download failed, no cache).", category.Name);
            }
        }

        _store.Replace(all);
        _log.LogInformation("Blocklist refresh done: {Total} domains across {Live} categories.",
            _store.Count, liveCategories);
    }

    /// <summary>
    /// Turn one raw list line into a bare domain, or null if it's a comment / blank / not a domain.
    /// Handles hosts format, plain domains, inline comments and wildcard prefixes.
    /// </summary>
    internal static string? ParseLine(string raw)
    {
        string line = raw;

        // Strip inline "# comment".
        int hash = line.IndexOf('#');
        if (hash >= 0)
            line = line[..hash];

        line = line.Trim();
        if (line.Length == 0 || line[0] is '!' or ';')
            return null;

        // hosts format puts the IP first, domain last; plain lists have just the domain.
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string token = parts[^1].TrimEnd('.').ToLowerInvariant();

        // Strip wildcard prefixes like "*.foo.com" or ".foo.com".
        if (token.StartsWith("*."))
            token = token[2..];
        if (token.StartsWith("."))
            token = token[1..];

        if (token.Length == 0 || token == "localhost" || token.Contains('/'))
            return null;
        if (!token.Contains('.'))
            return null;
        // Skip bare IPs (e.g. a lone "0.0.0.0").
        if (token.All(c => char.IsDigit(c) || c == '.'))
            return null;
        // Only sane domain characters.
        foreach (char c in token)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '.' or '_'))
                return null;

        return token;
    }
}
