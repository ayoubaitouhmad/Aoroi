namespace ParentalGuard.Service.Dns;

/// <summary>
/// The set of domains we refuse to resolve, plus the matching logic.
///
/// It's an in-memory <see cref="HashSet{T}"/> of domain names for O(1) lookups. The important
/// behaviour is <see cref="IsBlocked"/>: it blocks a listed domain AND all of its subdomains.
/// If "pornhub.com" is on the list, then "www.pornhub.com" and "cdn.pornhub.com" are blocked
/// too — because we check the name and each of its parent suffixes.
///
/// The store starts with a tiny <see cref="BundledFallback"/> so there is baseline protection
/// before any download finishes (the offline fallback half of the Step 4 strategy). The
/// <see cref="BlocklistUpdater"/> then calls <see cref="Replace"/> to swap in the full category
/// lists once they're loaded from cache or downloaded.
/// </summary>
public sealed class BlocklistStore
{
    /// <summary>
    /// A small built-in baseline used until the real category lists load, and whenever the
    /// machine is offline with no cache. Intentionally tiny — real coverage comes from the
    /// downloaded lists; this just guarantees the most obvious domains are never wide open.
    /// </summary>
    public static readonly IReadOnlyList<string> BundledFallback = new[]
    {
        // adult
        "pornhub.com", "xvideos.com", "xnxx.com", "xhamster.com",
        // gambling
        "bet365.com", "stake.com", "1xbet.com"
    };

    // volatile + whole-set replacement = lock-free reads. The DNS loop only ever reads this;
    // the updater swaps the whole reference atomically via Replace().
    private volatile HashSet<string> _blocked;

    public BlocklistStore()
    {
        _blocked = Build(BundledFallback);
    }

    /// <summary>How many domains are currently loaded.</summary>
    public int Count => _blocked.Count;

    /// <summary>
    /// True if this domain, or any parent domain of it, is on the blocklist.
    /// e.g. with "pornhub.com" listed: "www.pornhub.com" → check "www.pornhub.com",
    /// then "pornhub.com" (match) → blocked.
    /// </summary>
    public bool IsBlocked(string domain)
    {
        string current = Normalize(domain);
        if (current.Length == 0)
            return false;

        HashSet<string> set = _blocked; // snapshot the reference once

        while (true)
        {
            if (set.Contains(current))
                return true;

            int dot = current.IndexOf('.');
            if (dot < 0)
                return false;

            // Drop the left-most label and test the parent domain.
            current = current[(dot + 1)..];
        }
    }

    /// <summary>Replace the entire blocklist atomically (used by the updater on refresh).</summary>
    public void Replace(IEnumerable<string> domains) => _blocked = Build(domains);

    private static HashSet<string> Build(IEnumerable<string> domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in domains)
        {
            var n = Normalize(d);
            if (n.Length > 0)
                set.Add(n);
        }
        return set;
    }

    private static string Normalize(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
