namespace ParentalGuard.Service.Dns;

/// <summary>
/// STEP 3 — the set of domains we refuse to resolve, plus the matching logic.
///
/// It's an in-memory <see cref="HashSet{T}"/> of domain names for O(1) lookups. The important
/// behaviour is <see cref="IsBlocked"/>: it blocks a listed domain AND all of its subdomains.
/// If "pornhub.com" is on the list, then "www.pornhub.com" and "cdn.pornhub.com" are blocked
/// too — because we check the name and each of its parent suffixes.
///
/// For Step 3 it's seeded with a tiny built-in starter list, just enough to prove blocking
/// works. In Step 4 this same list becomes the "bundled fallback", and <see cref="Replace"/>
/// swaps in the big category lists downloaded from upstream.
/// </summary>
public sealed class BlocklistStore
{
    // volatile + whole-set replacement = lock-free reads. The DNS loop only ever reads this;
    // Step 4's refresh will swap the whole reference atomically via Replace().
    private volatile HashSet<string> _blocked;

    public BlocklistStore()
    {
        _blocked = BuildStarterList();
    }

    /// <summary>How many domains are currently loaded.</summary>
    public int Count => _blocked.Count;

    /// <summary>
    /// True if this domain, or any parent domain of it, is on the blocklist.
    /// e.g. with "example.com" listed: "a.b.example.com" → check "a.b.example.com",
    /// then "b.example.com", then "example.com" (match) → blocked.
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

    /// <summary>Replace the entire blocklist atomically (used by the Step 4 refresh).</summary>
    public void Replace(IEnumerable<string> domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in domains)
        {
            var n = Normalize(d);
            if (n.Length > 0)
                set.Add(n);
        }
        _blocked = set;
    }

    private static HashSet<string> BuildStarterList()
    {
        // Minimal seed list for testing + offline fallback. Replaced by real category lists
        // in Step 4. "example.com" is here on purpose: it normally resolves, so it's an easy
        // before/after test (blocked here, resolves once you remove it).
        var seed = new[]
        {
            "example.com",
            "doubleclick.net",
            "pornhub.com",
            "bet365.com"
        };

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in seed)
            set.Add(Normalize(d));
        return set;
    }

    private static string Normalize(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
