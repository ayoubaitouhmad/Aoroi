using System.Text.Json;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Service;

/// <summary>
/// Loads and saves <see cref="PolicyConfig"/> as JSON. On first run, writes the default config
/// so the parent has a file to edit. Re-reads from disk each time so edits made by the (future)
/// web panel take effect without restarting the Service.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public PolicyConfig Load()
    {
        if (!File.Exists(_path))
        {
            var fresh = PolicyConfig.Default();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<PolicyConfig>(json, JsonOptions);
            return cfg ?? PolicyConfig.Default();
        }
        catch (Exception)
        {
            // A corrupt or half-written file must never take enforcement offline.
            // Fall back to defaults rather than crashing the Service.
            return PolicyConfig.Default();
        }
    }

    public void Save(PolicyConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        // Write-and-rename so a reader never sees a half-written file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
