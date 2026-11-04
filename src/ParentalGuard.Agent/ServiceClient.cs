using System.IO.Pipes;
using System.Text;
using ParentalGuard.Core.Ipc;
using ParentalGuard.Core.Models;

namespace ParentalGuard.Agent;

/// <summary>
/// The Agent's end of the named pipe. Connects to the Service, sends a Heartbeat, and reads
/// back the UsageSnapshot. Reconnects transparently if the Service restarts. The Agent is the
/// "dumb" side: it never counts or decides anything, it just reports and obeys.
/// </summary>
public sealed class ServiceClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true })
            return;

        Cleanup();

        _pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: IpcContract.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await _pipe.ConnectAsync(5000, ct);

        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    /// <summary>
    /// Send one heartbeat and await the Service's snapshot. Returns null if the Service is
    /// unreachable (e.g. mid-restart); the caller keeps the session usable and retries next tick.
    /// </summary>
    public async Task<UsageSnapshot?> ExchangeAsync(Heartbeat hb, CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            await _writer!.WriteLineAsync(IpcContract.Serialize(hb));

            var line = await _reader!.ReadLineAsync(ct);
            if (line is null)
            {
                Cleanup();
                return null;
            }

            return IpcContract.Deserialize<UsageSnapshot>(line);
        }
        catch (Exception)
        {
            // Service down or pipe broken: drop the connection and let the next tick retry.
            Cleanup();
            return null;
        }
    }

    private void Cleanup()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose() => Cleanup();
}
