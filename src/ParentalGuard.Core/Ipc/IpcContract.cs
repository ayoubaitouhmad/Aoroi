using System.Text.Json;

namespace ParentalGuard.Core.Ipc;

/// <summary>
/// Shared constants and serialization settings for the named-pipe channel between
/// the Agent (child session) and the Service (session 0, running as LocalSystem).
///
/// Why a named pipe: it is local-only (never touches the network), fast, and Windows
/// lets us lock down who may connect with a pipe ACL. The Agent connects as the child;
/// the Service runs as SYSTEM and accepts the connection.
/// </summary>
public static class IpcContract
{
    /// <summary>Bump this whenever Heartbeat or UsageSnapshot change shape.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Pipe name. The "." server prefix is added by the client when connecting
    /// (<c>new NamedPipeClientStream(".", PipeName, ...)</c>).
    /// </summary>
    public const string PipeName = "ParentalGuard.Ipc.v1";

    /// <summary>One JSON message per line; we read/write newline-delimited UTF-8.</summary>
    public const char MessageTerminator = '\n';

    /// <summary>Shared JSON options so both ends encode/decode identically.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
