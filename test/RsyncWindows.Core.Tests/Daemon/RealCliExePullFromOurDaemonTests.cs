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

using System.Diagnostics;
using System.Net;
using RsyncWindows.Core.Daemon;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

/// <summary>Mirror of <see cref="RealCliExeToOurDaemonTests"/> for the PULL direction (real
/// published rsyncWindows.exe pulling FROM an in-process daemon) -- the push-side bugs
/// (amSender polarity, missing preNegotiatedVersion, empty-remotePath/terminator collision) were
/// all found and fixed in pairs across push/pull, but only the push direction had actually been
/// exercised end-to-end before; this is what pull needs too.</summary>
public class RealCliExePullFromOurDaemonTests
{
    private const string CliExePath = @"C:\PhpProjects\rsyncWindows\dist\bin\cli\rsyncWindows.exe";

    [Fact]
    public async Task RealPublishedExe_PullsFromModuleRoot_ReconstructsCorrectly()
    {
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: {CliExePath} not found -- publish the Cli project first");
            return;
        }

        string moduleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-realexe-pull-mod-" + Guid.NewGuid().ToString("N")[..8]);
        string destDir = Path.Combine(Path.GetTempPath(), "rsyncwin-realexe-pull-dst-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(moduleDir);
        Directory.CreateDirectory(destDir);
        byte[] content = "hello, pulled from the REAL published exe"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(moduleDir, "greeting.txt"), content);

        var config = RsyncdConfig.Parse(["[pullmod]", $"path = {moduleDir}", "read only = yes"]);
        var errors = new List<string>();

        void HandleConnection(DuplexStreamPair raw, IPAddress clientAddress)
        {
            try
            {
                var request = DaemonHandshake.RunServerSide(raw, config, clientAddress);
                if (request == null)
                    return;
                var serverSession = ProtocolNegotiator.Negotiate(raw, isServer: true, preNegotiatedVersion: request.RemoteProtocolVersion, compress: request.Compress);

                // Mirrors DaemonConnectionHandler.RunAsSender's RecvFilterList: the generator
                // peer unconditionally sends a filter-list terminator before we may start
                // walking/sending our own flist (see ReceiverSession.RunReceiverLoop's matching
                // send, the actual fix this test exists to verify).
                while (WireCodec.ReadInt32(serverSession.Input) != 0) { }

                // Mirrors DaemonConnectionHandler.RunAsSender's own argv-derived preserveUid/Gid
                // (the client's -a/-o/-g flags, as sent via its bundled short-flags argv) --
                // hardcoding false here (as an earlier version of this test did) doesn't
                // reproduce the real bug this test now also covers: the flist ENCODE side
                // (here) and the CLIENT's own DECODE side must agree, or FileListDecoder desyncs
                // on the very first entry.
                bool preserveUid = request.ServerArgs.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.TakeWhile(c => c != 'e').Contains('o'));
                bool preserveGid = request.ServerArgs.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.TakeWhile(c => c != 'e').Contains('g'));

                var entries = FileListBuilder.Walk(moduleDir).ToList();
                var encoder = new FileListEncoder();
                foreach (var entry in entries)
                    encoder.Write(serverSession.Output, entry, preserveUid, preserveGid);
                encoder.WriteEndOfList(serverSession.Output);
                // Mirrors DaemonConnectionHandler.RunAsSender's own IdListCodec.WriteIdLists call
                // -- the real client's ReceiverSession.RunReceiverLoop always expects this block
                // right after the flist whenever -o/-g is active (see IdListCodec's doc comment).
                IdListCodec.WriteIdLists(serverSession.Output, entries, preserveUid, preserveGid);
                serverSession.Output.Flush();

                var files = entries
                    .Select(e => new SenderSession.FileToSend(e, e.FileType == RsyncFileType.Regular ? File.ReadAllBytes(Path.Combine(moduleDir, e.Path)) : []))
                    .ToList();
                SenderSession.RunSenderLoop(serverSession.Input, serverSession.Output, files, serverSession.ChecksumSeed, writeDaemonStats: true);
                serverSession.Output.Flush();
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
            var psi = new ProcessStartInfo(CliExePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-av"); // -a (archive, implies -o/-g) is what surfaced the preserveUid/Gid bug
            psi.ArgumentList.Add($"rsync://127.0.0.1:{port}/pullmod/");
            psi.ArgumentList.Add(destDir + "\\");

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited = proc.WaitForExit(10_000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            string stdout = exited ? await stdoutTask : "(timed out)";
            string stderr = exited ? await stderrTask : "(timed out)";

            Console.WriteLine($"exited: {exited}, exit code: {(exited ? proc.ExitCode : -1)}");
            Console.WriteLine($"stdout: {stdout}");
            Console.WriteLine($"stderr: {stderr}");
            Console.WriteLine($"server errors: {string.Join(" | ", errors)}");

            Assert.True(exited, "rsyncWindows.exe pull did not exit within 10s (hung)");
            Assert.Empty(errors);
            Assert.Equal(0, proc.ExitCode);
            string landedPath = Path.Combine(destDir, "greeting.txt");
            Assert.True(File.Exists(landedPath), $"expected file at {landedPath}");
            Assert.Equal(content, await File.ReadAllBytesAsync(landedPath));
        }
        finally
        {
            await serverTransport.StopAsync();
            Directory.Delete(moduleDir, recursive: true);
            Directory.Delete(destDir, recursive: true);
        }
    }
}
