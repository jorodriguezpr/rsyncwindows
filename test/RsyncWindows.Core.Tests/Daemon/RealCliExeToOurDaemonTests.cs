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
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

/// <summary>
/// Spawns the REAL published rsyncWindows.exe (dist/bin/cli) as a subprocess, pointed at a
/// fast in-process daemon (not a real Windows Service -- no elevation needed, runs in
/// milliseconds). This is deliberately NOT the same as <see cref="OurClientToOurDaemonTests"/>,
/// which hand-assembles the same sequence of calls <c>Cli.ClientRunner</c> makes rather than
/// actually running it -- that difference is exactly what let a real bug slip past that test:
/// <see cref="RsyncWindows.Core.Options.ServerInvocationBuilderTests"/> has the full writeup, but
/// in short, `Cli.ClientRunner.RunDaemonPush`'s OWN argv-building (via
/// <c>ServerInvocationBuilder.BuildServerArgs</c>) used to send an empty final argument for the
/// extremely common "push to the module's own root" case, which collided with the argv list's
/// own NUL-terminator encoding -- invisible to a test that never calls that real code path.
/// This class exists specifically to keep exercising the REAL exe end-to-end, not a proxy for it.
/// </summary>
public class RealCliExeToOurDaemonTests
{
    private const string CliExePath = @"C:\PhpProjects\rsyncWindows\dist\bin\cli\rsyncWindows.exe";

    private sealed record PushResult(int ExitCode, string Stdout, string Stderr, IReadOnlyList<string> ServerErrors, byte[]? LandedContent);

    private static async Task<PushResult> PushToModuleRoot(string moduleUrlSuffix)
    {
        string moduleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-realexe-ourdaemon-mod-" + Guid.NewGuid().ToString("N")[..8]);
        string srcDir = Path.Combine(Path.GetTempPath(), "rsyncwin-realexe-ourdaemon-src-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(moduleDir);
        Directory.CreateDirectory(srcDir);
        byte[] content = "hello from the REAL published exe"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "hello.txt"), content);

        var config = RsyncdConfig.Parse(["[pushmod]", $"path = {moduleDir}", "read only = no"]);
        var errors = new List<string>();

        void HandleConnection(DuplexStreamPair raw, IPAddress clientAddress)
        {
            try
            {
                var request = DaemonHandshake.RunServerSide(raw, config, clientAddress);
                if (request == null)
                    return;
                var serverSession = ProtocolNegotiator.Negotiate(raw, isServer: true, preNegotiatedVersion: request.RemoteProtocolVersion, compress: request.Compress);
                // Mirrors DaemonConnectionHandler.RunAsReceiver's own argv-derived preserveUid/Gid.
                bool preserveUid = request.ServerArgs.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.TakeWhile(c => c != 'e').Contains('o'));
                bool preserveGid = request.ServerArgs.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.TakeWhile(c => c != 'e').Contains('g'));
                var received = ReceiverSession.RunReceiverLoop(serverSession.Input, serverSession.Output,
                    path => File.Exists(Path.Combine(moduleDir, path)) ? File.ReadAllBytes(Path.Combine(moduleDir, path)) : [],
                    serverSession.ChecksumSeed, preserveUid: preserveUid, preserveGid: preserveGid);
                foreach (var file in received)
                    File.WriteAllBytes(Path.Combine(moduleDir, file.Entry.Path), file.Data);
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
            psi.ArgumentList.Add("-av"); // -a implies -o/-g, exercising the preserveUid/Gid path too
            psi.ArgumentList.Add(srcDir + "\\");
            psi.ArgumentList.Add($"rsync://127.0.0.1:{port}/pushmod{moduleUrlSuffix}");

            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(10_000);

            string landedPath = Path.Combine(moduleDir, "hello.txt");
            byte[]? landed = File.Exists(landedPath) ? await File.ReadAllBytesAsync(landedPath) : null;
            return new PushResult(proc.ExitCode, stdout, stderr, errors, landed);
        }
        finally
        {
            await serverTransport.StopAsync();
            Directory.Delete(moduleDir, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("/")]  // rsync://host/module/ -- trailing slash, no subpath
    [InlineData("")]   // rsync://host/module  -- no trailing slash at all
    public async Task RealPublishedExe_PushesToModuleRoot_ReconstructsCorrectly(string urlSuffix)
    {
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: {CliExePath} not found -- publish the Cli project first");
            return;
        }

        var result = await PushToModuleRoot(urlSuffix);

        Console.WriteLine($"exit code: {result.ExitCode}");
        Console.WriteLine($"stdout: {result.Stdout}");
        Console.WriteLine($"stderr: {result.Stderr}");

        Assert.Empty(result.ServerErrors);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.LandedContent);
        Assert.Equal("hello from the REAL published exe"u8.ToArray(), result.LandedContent);
    }
}
