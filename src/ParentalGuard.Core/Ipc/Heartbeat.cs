using ParentalGuard.Core.Models;

namespace ParentalGuard.Core.Ipc;

/// <summary>
/// Message the Agent sends to the Service on every tick (default: once per 15 seconds).
/// It reports who is active and in what state; the Service replies with a <see cref="UsageSnapshot"/>.
/// </summary>
public sealed record Heartbeat
{
    /// <summary>Windows account name of the interactive session the Agent is running in.</summary>
    public required string UserName { get; init; }

    /// <summary>The session's current activity, as judged inside that session.</summary>
    public required ActivityState State { get; init; }

    /// <summary>Seconds since the last keyboard/mouse input in this session.</summary>
    public int IdleSeconds { get; init; }

    /// <summary>Local timestamp the Agent sampled at (the Service trusts its own clock for counting,
    /// but keeps this for diagnostics and clock-tamper detection).</summary>
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Protocol version so Service and Agent can refuse to talk if mismatched after an update.</summary>
    public int ProtocolVersion { get; init; } = IpcContract.ProtocolVersion;
}
