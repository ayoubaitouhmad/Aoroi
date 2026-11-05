namespace ParentalGuard.Service.Dns;

/// <summary>
/// STEP 5 — the map from a search/video domain to the provider's "forced-safe" hostname.
///
/// Each major provider publishes a special hostname that always serves filtered results:
///   - Google search  → forcesafesearch.google.com   (SafeSearch on)
///   - YouTube         → restrict.youtube.com (strict) / restrictmoderate.youtube.com (moderate)
///   - Bing            → strict.bing.com
///   - DuckDuckGo      → safe.duckduckgo.com
///
/// We enforce it at the DNS layer by answering lookups for the normal domain with a CNAME to the
/// safe host. The browser then resolves the safe host (through us) and connects there, so the
/// filtered results are served and the child can't switch SafeSearch off in the UI.
/// </summary>
public static class SafeSearchRules
{
    /// <summary>
    /// The safe host to CNAME <paramref name="domain"/> to, or null if it isn't a search/video
    /// domain we rewrite. <paramref name="youTubeMode"/> is "strict", "moderate", or "off".
    /// </summary>
    public static string? GetSafeHost(string domain, string youTubeMode)
    {
        string d = Normalize(domain);

        // YouTube first (its hosts are specific and must not fall through to the Google rule).
        if (IsYouTube(d))
        {
            return youTubeMode?.ToLowerInvariant() switch
            {
                "off" => null,
                "moderate" => "restrictmoderate.youtube.com",
                _ => "restrict.youtube.com" // strict (default)
            };
        }

        // Strip a leading "www." for the remaining exact-ish matches.
        string bare = d.StartsWith("www.") ? d[4..] : d;

        // Google search across any country TLD: google.com, google.fr, google.co.ma, ...
        // (bare second-level label is "google"). mail./drive./accounts. are NOT matched
        // because they keep their subdomain and don't start with "google.".
        if (bare.StartsWith("google."))
            return "forcesafesearch.google.com";

        if (bare == "bing.com")
            return "strict.bing.com";

        if (bare == "duckduckgo.com")
            return "safe.duckduckgo.com";

        return null;
    }

    private static bool IsYouTube(string normalized) => normalized is
        "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"
        or "youtube-nocookie.com" or "www.youtube-nocookie.com"
        or "youtubei.googleapis.com" or "youtube.googleapis.com";

    private static string Normalize(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();
}
