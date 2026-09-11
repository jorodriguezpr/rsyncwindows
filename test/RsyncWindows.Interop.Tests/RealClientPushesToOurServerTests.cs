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
using System.Net.Sockets;
using System.Text;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using Xunit;

namespace RsyncWindows.Interop.Tests;

/// <summary>
/// Real interop, the harder direction (Phase 4b per the plan): a REAL rsync client (the
/// binary built under WSL2) pushes a file to OUR C# code playing the server/generator/receiver
/// role, proving wire-compatibility from the other side's perspective -- this is where gaps in
/// our own protocol implementation (as opposed to gaps in understanding real rsync's behavior,
/// which Phase 4a mostly exercised) are most likely to surface, since here WE have to parse
/// and correctly respond to whatever the real, unmodified rsync client actually sends.
///
/// No SSH server is used or needed. Real rsync's `--rsh` option is repurposed: rsync's own
/// do_cmd() (main.c:517) always execs `&lt;rsh-tokens&gt; &lt;machine&gt; &lt;rsync-path&gt; --server ...`,
/// so pointing `--rsh` at tools/interop_rsh_connector.py (a plain stdio&lt;-&gt;TCP relay) makes
/// rsync's client connect straight to our C# `TcpListener` on the Windows host, with no
/// daemon greeting -- exactly the same SSH-piped wire shape Phase 4a's SenderSession already
/// proved, just in the opposite direction. See that script's doc comment for the exact
/// command line and how the Windows-host IP is found from WSL2.
///
/// Requires the same WSL2 + built rsync-3.5.0 binary as Phase 4a; skipped automatically if
/// unavailable, since this is opt-in/manual per the plan.
/// </summary>
public class RealClientPushesToOurServerTests
{
    private const string RsyncBinaryPath = "/home/jorodriguez/rsync-build/rsync";
    private const int ServerPort = 17901;

    private static bool RealRsyncAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", $"-e test -x {RsyncBinaryPath}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a command under WSL with a REAL enforced timeout. `Process.WaitForExit(ms)` alone
    /// is not enough here -- `StandardOutput.ReadToEnd()` blocks until the child closes its
    /// stdout, which never happens if the process is genuinely stuck (e.g. rsync hung waiting
    /// on a network read that never resolves), so a naive ReadToEnd()-then-WaitForExit(timeout)
    /// sequence never reaches the timeout at all. This reads output on a background task and
    /// kills the whole process tree if it doesn't finish in time.
    /// </summary>
    private static string RunWslCapture(string bashCommand, int timeoutMs = 20_000)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(bashCommand);

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(timeoutMs))
        {
            Console.WriteLine($"[wsl] TIMED OUT after {timeoutMs}ms, killing process tree: {bashCommand}");
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            p.WaitForExit(5000);
        }

        string err = stderrTask.Wait(2000) ? stderrTask.Result : "(stderr read timed out)";
        if (err.Length > 0)
            Console.WriteLine("[wsl stderr] " + err);
        return stdoutTask.Wait(2000) ? stdoutTask.Result : "";
    }

    private static void RunWsl(string bashCommand) => RunWslCapture(bashCommand);

    [Fact]
    public void RealRsyncClient_PushesFile_ToOurServer_ReconstructsCorrectly()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        byte[] content = Encoding.UTF8.GetBytes("Hello INTO RsyncWindows from a real rsync client (Phase 4b)!\n");
        string remoteFileName = "phase4b-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        string wslSourcePath = "/tmp/" + remoteFileName;
        RunWsl($"printf '%s' '{Encoding.UTF8.GetString(content)}' > {wslSourcePath}");

        var listener = new TcpListener(IPAddress.Any, ServerPort);
        listener.Start();
        try
        {
            IReadOnlyList<ReceiverSession.ReceivedFile>? received = null;
            Exception? serverException = null;
            var serverTask = Task.Run(() =>
            {
                try
                {
                    using var tcpClient = listener.AcceptTcpClient();
                    var stream = tcpClient.GetStream();
                    var raw = new DuplexStreamPair(stream, stream);
                    var session = ProtocolNegotiator.Negotiate(raw, isServer: true,
                        onMessage: (code, payload) => Console.WriteLine($"[server-side msg] {code}: {Encoding.UTF8.GetString(payload)}"));
                    received = ReceiverSession.RunReceiverLoop(session.Input, session.Output, _ => [], session.ChecksumSeed);
                }
                catch (Exception ex)
                {
                    serverException = ex;
                }
            });

            Console.WriteLine($"[test] windowsHostIp={windowsHostIp} listening on 0.0.0.0:{ServerPort}");
            string connectorPath = "/mnt/c/PhpProjects/rsyncWindows/tools/interop_rsh_connector.py";
            string rsh = $"python3 {connectorPath} {windowsHostIp} {ServerPort}";
            string cmd = $"{RsyncBinaryPath} -v --rsh=\"{rsh}\" {wslSourcePath} fakehost:{remoteFileName}";
            Console.WriteLine("[test] running: " + cmd);
            string clientOutput = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + clientOutput);

            bool completed = serverTask.Wait(TimeSpan.FromSeconds(15));
            Assert.True(completed, "server-side loop did not complete within timeout");
            if (serverException != null)
                throw new Exception("server-side ReceiverSession threw", serverException);

            var file = Assert.Single(received!);
            Assert.Equal(remoteFileName, file.Entry.Path);
            Assert.Equal(content, file.Data);
        }
        finally
        {
            listener.Stop();
            RunWsl($"rm -f {wslSourcePath}");
        }
    }

    /// <summary>
    /// Phase 6 real interop: a REAL rsync client's `-z` pushes a file to OUR server, proving our
    /// <see cref="RsyncWindows.Core.Delta.CompressedTokenReader"/> can decode a genuine zlib
    /// sender's compressed literal stream -- not just our own writer's output (which
    /// `CompressedDeltaRoundTripTests` already covers). This is the interop risk this project's
    /// Phase 6 memory flagged as the single biggest open question, now that both sides are
    /// backed by real zlib (see <see cref="RsyncWindows.Core.Delta.Zlib.RawInflater"/>'s doc
    /// comment for why a prior SharpZipLib-based revision couldn't be trusted here even in
    /// self-consistency, let alone against a real peer).
    /// </summary>
    [Fact]
    public void RealRsyncClient_PushesCompressedFile_ToOurServer_ReconstructsCorrectly()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        // A few KB of moderately-redundant text (not a single repeated byte, not pure random) --
        // representative enough to force real compression to engage meaningfully, matching the
        // kind of content that exposed the SharpZipLib bug in-process.
        var textBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
            textBuilder.AppendLine($"Line {i}: the quick brown fox jumps over the lazy dog {i * 7}.");
        byte[] content = Encoding.UTF8.GetBytes(textBuilder.ToString());

        string remoteFileName = "phase6-z-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        string wslSourcePath = "/tmp/" + remoteFileName;
        RunWsl($"printf '%s' '{Encoding.UTF8.GetString(content).Replace("'", "'\\''")}' > {wslSourcePath}");

        var listener = new TcpListener(IPAddress.Any, ServerPort);
        listener.Start();
        try
        {
            IReadOnlyList<ReceiverSession.ReceivedFile>? received = null;
            Exception? serverException = null;
            var serverTask = Task.Run(() =>
            {
                try
                {
                    using var tcpClient = listener.AcceptTcpClient();
                    var stream = tcpClient.GetStream();
                    var raw = new DuplexStreamPair(stream, stream);
                    var session = ProtocolNegotiator.Negotiate(raw, isServer: true, compress: true,
                        onMessage: (code, payload) => Console.WriteLine($"[server-side msg] {code}: {Encoding.UTF8.GetString(payload)}"));
                    Console.WriteLine($"[test] negotiated CompressionEnabled={session.CompressionEnabled}");
                    received = ReceiverSession.RunReceiverLoop(session.Input, session.Output, _ => [], session.ChecksumSeed, compress: session.CompressionEnabled);
                }
                catch (Exception ex)
                {
                    serverException = ex;
                }
            });

            Console.WriteLine($"[test] windowsHostIp={windowsHostIp} listening on 0.0.0.0:{ServerPort}");
            string connectorPath = "/mnt/c/PhpProjects/rsyncWindows/tools/interop_rsh_connector.py";
            string rsh = $"python3 {connectorPath} {windowsHostIp} {ServerPort}";
            string cmd = $"{RsyncBinaryPath} -vz --rsh=\"{rsh}\" {wslSourcePath} fakehost:{remoteFileName}";
            Console.WriteLine("[test] running: " + cmd);
            string clientOutput = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + clientOutput);

            bool completed = serverTask.Wait(TimeSpan.FromSeconds(15));
            Assert.True(completed, "server-side loop did not complete within timeout");
            if (serverException != null)
                throw new Exception("server-side ReceiverSession threw", serverException);

            var file = Assert.Single(received!);
            Assert.Equal(remoteFileName, file.Entry.Path);
            Assert.Equal(content, file.Data);
        }
        finally
        {
            listener.Stop();
            RunWsl($"rm -f {wslSourcePath}");
        }
    }
}
