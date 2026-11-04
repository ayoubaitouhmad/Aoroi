using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using ParentalGuard.Core.Ipc;

namespace ParentalGuard.Service.Ipc;

/// <summary>
/// Accepts named-pipe connections from Agents running in interactive sessions, reads one
/// Heartbeat per line, hands it to the <see cref="UsageTracker"/>, and writes back the
/// resulting UsageSnapshot as one JSON line.
///
/// The pipe is created with an ACL that lets authenticated users connect (so any child session
/// can report in) while the Service itself runs as SYSTEM. We create a fresh server stream per
/// connection so several sessions (e.g. fast user switching) can be served; this loop handles
/// them one at a time, which is plenty for heartbeat-rate traffic.
/// </summary>
public sealed class PipeServer
{
    private readonly UsageTracker _tracker;
    private readonly ILogger<PipeServer> _log;

    public PipeServer(UsageTracker tracker, ILogger<PipeServer> log)
    {
        _tracker = tracker;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("IPC pipe server starting on \\\\.\\pipe\\{Pipe}", IpcContract.PipeName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(ct);
                await HandleConnectionAsync(server, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IPC connection error; continuing");
                // Brief backoff so a persistent fault doesn't spin the CPU.
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }

        _log.LogInformation("IPC pipe server stopped");
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        // Allow authenticated users to read/write/connect; deny everyone else by not granting it.
        var security = new PipeSecurity();
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(
            authenticatedUsers,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        // SYSTEM (us) keeps full control.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcContract.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        // One connection may carry many heartbeats until the Agent disconnects.
        while (server.IsConnected && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // client closed

            Heartbeat? hb;
            try
            {
                hb = IpcContract.Deserialize<Heartbeat>(line);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Bad heartbeat payload");
                continue;
            }

            if (hb is null || hb.ProtocolVersion != IpcContract.ProtocolVersion)
            {
                _log.LogWarning("Dropping heartbeat: null or protocol mismatch (got {V})",
                    hb?.ProtocolVersion);
                continue;
            }

            var snapshot = _tracker.Process(hb);
            await writer.WriteLineAsync(IpcContract.Serialize(snapshot));
        }
    }
}
