namespace ParentalGuard.Service;

/// <summary>
/// Central place for every on-disk path the Service uses. Everything lives under
/// %ProgramData%\ParentalGuard, which by default is writable only by administrators and
/// SYSTEM — so the child (a standard user) can read nothing here and tamper with nothing.
/// </summary>
public static class ServicePaths
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ParentalGuard");

    public static string ConfigFile => Path.Combine(RootDir, "policy.json");

    public static string DatabaseFile => Path.Combine(RootDir, "usage.db");

    public static string LogDir => Path.Combine(RootDir, "logs");

    /// <summary>Where downloaded category blocklists are cached (one .txt per category).</summary>
    public static string BlocklistCacheDir => Path.Combine(RootDir, "blocklists");

    /// <summary>Ensure the directory tree exists. Called once at startup.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(BlocklistCacheDir);
    }
}
