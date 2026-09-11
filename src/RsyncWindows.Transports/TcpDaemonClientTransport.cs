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

using System.Net.Sockets;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Transports;

/// <summary>
/// TCP client transport for connecting to an rsync daemon (`rsync://host[:port]/module/path`
/// or `host::module/path`, historically port 873). Just opens the socket and hands back a
/// <see cref="DuplexStreamPair"/> -- the caller drives <see cref="Daemon.DaemonHandshake.RunClientSide"/>
/// then <see cref="ProtocolNegotiator"/>, same layering as <see cref="SshProcessTransport"/>.
/// </summary>
public sealed class TcpDaemonClientTransport(string host, int port = 873) : IDisposable
{
    private TcpClient? _client;

    public const int DefaultPort = 873;

    public DuplexStreamPair Connect()
    {
        _client = new TcpClient();
        _client.Connect(host, port);
        _client.NoDelay = true;
        var stream = _client.GetStream();
        return new DuplexStreamPair(stream, stream);
    }

    public void Dispose() => _client?.Dispose();
}
