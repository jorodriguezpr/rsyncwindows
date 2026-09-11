// rsyncWindows
// Developer: Jose Rodriguez Arroyo
// Email: jrpcone@gmail.com
// GitHub: https://github.com/jorodriguezpr
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Net;
using System.Net.Sockets;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Transports;

/// <summary>
/// TCP listener for rsync's daemon mode (historically port 873, `rsync://host/module/path`) --
/// the counterpart to <see cref="SshProcessTransport"/>'s process-spawn transport for SSH-piped
/// mode. Purely a socket-accept loop: per the plan's "Core never touches Process/Socket"
/// design rule, this project's only job is handing each accepted connection's raw duplex stream
/// (plus the peer's address, needed for `hosts allow`/`hosts deny` and log lines) to a
/// caller-supplied handler -- the actual daemon protocol (<see cref="Daemon.DaemonHandshake"/>,
/// <see cref="ProtocolNegotiator"/>, then a sender/receiver session) lives in Core and is
/// composed by the caller (<c>RsyncWindows.Service</c>).
///
/// One thread per connection (via <see cref="Task.Run(Action)"/>), matching how a real rsync
/// daemon forks a child per connection -- sender/receiver sessions are synchronous, blocking
/// stream calls throughout this codebase, so there's no async story to preserve within a
/// connection's handling.
/// </summary>
public sealed class TcpDaemonServerTransport(IPEndPoint endpoint, Action<DuplexStreamPair, IPAddress> handleConnection, Action<string>? log = null) : IDisposable
{
    private readonly TcpListener _listener = new(endpoint);
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break; // listener was stopped
            }

            _ = Task.Run(() => HandleClient(client), CancellationToken.None);
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var raw = new DuplexStreamPair(stream, stream);
                handleConnection(raw, remoteAddress);
            }
            catch (Exception ex)
            {
                // A single bad/dropped connection must not take down the listener or other
                // connections -- the handler owns its own protocol-level error reporting to the
                // peer (e.g. an "@ERROR:" line or MSG_ERROR frame); this is the last-resort catch.
                log?.Invoke($"connection from {remoteAddress} ended with an error: {ex.Message}");
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}
