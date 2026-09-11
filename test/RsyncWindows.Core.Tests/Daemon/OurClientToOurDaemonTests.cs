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
using RsyncWindows.Core.Daemon;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Options;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

/// <summary>
/// Our own client-initiated daemon push, over a REAL TCP socket (not LoopbackDuplexTransport),
/// against our own daemon (TcpDaemonServerTransport + DaemonConnectionHandler) -- reproduces a
/// bug found running the actual installed rsyncWindows.exe against the actual installed service
/// for the first time: this exact combination (ClientRunner's daemon-push code path) had never
/// been exercised by any prior test. Every other daemon-mode test pairs one side with a REAL
/// rsync binary (which doesn't share whatever bug our own client-initiating code has) or drives
/// SenderSession/ProtocolNegotiator directly, bypassing this code path entirely.
/// </summary>
public class OurClientToOurDaemonTests
{
    [Fact]
    public async Task PushSingleFile_OverRealTcpSocket_ToOurOwnDaemon_Succeeds()
    {
        string moduleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-ourclient-ourdaemon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(moduleDir);
        var config = RsyncdConfig.Parse(
        [
            "[pushmod]",
            $"path = {moduleDir}",
            "read only = no",
        ]);

        // Inline daemon-connection composition (mirrors RsyncWindows.Service's own
        // DaemonConnectionHandler, which this test project doesn't reference) -- just enough of
        // the real composition root to exercise RunServerSide -> Negotiate -> ReceiverSession
        // against a real client over a real socket.
        var errors = new List<string>();
        void HandleConnection(DuplexStreamPair raw, IPAddress clientAddress)
        {
            try
            {
                var request = DaemonHandshake.RunServerSide(raw, config, clientAddress);
                if (request == null)
                    return;
                var serverSession = ProtocolNegotiator.Negotiate(raw, isServer: true, preNegotiatedVersion: request.RemoteProtocolVersion, compress: request.Compress);
                string root = config.Modules[0].Path;
                var received = ReceiverSession.RunReceiverLoop(serverSession.Input, serverSession.Output,
                    path => File.Exists(Path.Combine(root, path)) ? File.ReadAllBytes(Path.Combine(root, path)) : [],
                    serverSession.ChecksumSeed);
                foreach (var file in received)
                    File.WriteAllBytes(Path.Combine(root, file.Entry.Path), file.Data);
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex.ToString());
            }
        }

        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var serverTransport = new TcpDaemonServerTransport(endpoint, HandleConnection, msg => { lock (errors) errors.Add(msg); });
        serverTransport.Start();
        int port = serverTransport.LocalEndpoint.Port;

        try
        {
            byte[] content = "hello from our own client to our own daemon"u8.ToArray();
            var entry = new FileEntry
            {
                Path = "hello.txt",
                FileType = RsyncFileType.Regular,
                Mode = UnixMode.RegularFileDefault,
                Length = content.Length,
                ModTimeUnix = 0,
            };

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcpClient.GetStream();
            var raw = new DuplexStreamPair(stream, stream);

            var options = new RsyncOptions { Verbose = 1 };
            var serverArgs = ServerInvocationBuilder.BuildServerArgs(options, amSender: true, "hello.txt");
            int remoteVersion = DaemonHandshake.RunClientSide(raw, "pushmod", serverArgs);
            var session = ProtocolNegotiator.Negotiate(raw, isServer: false, preNegotiatedVersion: remoteVersion);

            var encoder = new FileListEncoder();
            encoder.Write(session.Output, entry, preserveUid: false, preserveGid: false);
            encoder.WriteEndOfList(session.Output);
            session.Output.Flush();

            var files = new List<SenderSession.FileToSend> { new(entry, content) };
            SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed);
            session.Output.Flush();
            tcpClient.Close();

            await Task.Delay(200); // let the server finish processing after the client closes

            Assert.Empty(errors);
            string landed = Path.Combine(moduleDir, "hello.txt");
            Assert.True(File.Exists(landed), $"expected file at {landed}");
            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }
        finally
        {
            await serverTransport.StopAsync();
            Directory.Delete(moduleDir, recursive: true);
        }
    }
}
