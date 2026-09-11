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
using Xunit;

namespace RsyncWindows.Interop.Tests;

/// <summary>
/// The truest form of Phase 4b validation: a REAL rsync client (WSL2) pushes a file all the way
/// through to the ACTUAL COMPILED `rsyncWindows.exe`, running its `--server` role as a real
/// spawned Windows process -- not <see cref="RealClientPushesToOurServerTests"/>'s in-process
/// `ReceiverSession` call, which proves the protocol code is correct but never exercises
/// `Program.cs`/`Cli.ServerRole.cs` or the built binary itself.
///
/// Uses the same `--rsh=interop_rsh_connector.py` relay technique as the other Phase 4b tests,
/// but the Windows-side listener here spawns `rsyncWindows.exe --server` (fixed args matching a
/// plain `-v` push -- mirroring how `PushSingleFileToRealRsyncTests`' WSL-side bridge also uses
/// a fixed, pre-baked `--server` command line rather than relaying the client's real argv) with
/// its stdio wired directly to the accepted TCP connection -- exactly the way a real `ssh` would
/// wire a remote process's stdio to the SSH channel. Reads/writes the process's raw
/// `BaseStream`s (never the text-mode `StandardInput`/`StandardOutput` `TextReader`/`Writer`
/// wrappers), since those apply encoding/newline translation that would corrupt the binary
/// protocol stream.
/// </summary>
public class RealClientPushesToOurCliExecutableTests
{
    private const string RsyncBinaryPath = "/home/jorodriguez/rsync-build/rsync";
    private const int ServerPort = 17904;
    private const string CliExePath = @"C:\PhpProjects\rsyncWindows\src\RsyncWindows.Cli\bin\Debug\net10.0\rsyncWindows.exe";

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

    private static void Pump(Stream src, Stream dst)
    {
        try
        {
            var buf = new byte[65536];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                dst.Flush();
            }
        }
        catch (IOException)
        {
            // peer closed mid-read/write -- expected at end of transfer, not a real failure.
        }
    }

    [Fact]
    public void RealRsyncClient_PushesFile_ToOurSpawnedCliExecutable_ReconstructsCorrectly()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: rsyncWindows.exe not found at {CliExePath} -- build RsyncWindows.Cli first");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        byte[] content = Encoding.UTF8.GetBytes("Hello INTO the real rsyncWindows.exe --server process (Phase 4b, real binary)!\n");
        string remoteFileName = "phase4b-exe-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        string wslSourcePath = "/tmp/" + remoteFileName;
        RunWsl($"printf '%s' '{Encoding.UTF8.GetString(content)}' > {wslSourcePath}");

        string destDir = Path.Combine(Path.GetTempPath(), "rsyncwin-phase4b-exe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(destDir);

        var listener = new TcpListener(IPAddress.Any, ServerPort);
        listener.Start();
        try
        {
            var serverTask = Task.Run(() =>
            {
                using var tcpClient = listener.AcceptTcpClient();
                using var netStream = tcpClient.GetStream();

                var psi = new ProcessStartInfo(CliExePath)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // Fixed args matching a plain `-v` push's bundled short flags (see
                // ServerInvocationBuilder for the real shape) -- this is a PUSH, so no --sender.
                psi.ArgumentList.Add("--server");
                psi.ArgumentList.Add("-ve32.0fxCIvu");
                psi.ArgumentList.Add(".");
                psi.ArgumentList.Add(destDir);

                using var proc = Process.Start(psi)!;
                var stderrTask = proc.StandardError.ReadToEndAsync();

                var toProc = Task.Run(() => Pump(netStream, proc.StandardInput.BaseStream));
                var fromProc = Task.Run(() => Pump(proc.StandardOutput.BaseStream, netStream));

                proc.WaitForExit(15_000);
                toProc.Wait(2000);
                fromProc.Wait(2000);

                string stderr = stderrTask.Wait(2000) ? stderrTask.Result : "";
                if (stderr.Length > 0)
                    Console.WriteLine("[rsyncWindows.exe stderr] " + stderr);
                Console.WriteLine($"[test] rsyncWindows.exe --server exited with {proc.ExitCode}");
            });

            Console.WriteLine($"[test] windowsHostIp={windowsHostIp} listening on 0.0.0.0:{ServerPort}, spawning {CliExePath} --server on connect");
            string connectorPath = "/mnt/c/PhpProjects/rsyncWindows/tools/interop_rsh_connector.py";
            string rsh = $"python3 {connectorPath} {windowsHostIp} {ServerPort}";
            string cmd = $"{RsyncBinaryPath} -v --rsh=\"{rsh}\" {wslSourcePath} fakehost:{remoteFileName}";
            Console.WriteLine("[test] running: " + cmd);
            string clientOutput = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + clientOutput);

            bool completed = serverTask.Wait(TimeSpan.FromSeconds(20));
            Assert.True(completed, "spawned rsyncWindows.exe server-side loop did not complete within timeout");

            string landed = Path.Combine(destDir, remoteFileName);
            Assert.True(File.Exists(landed), $"expected file did not land at {landed}");
            byte[] actual = File.ReadAllBytes(landed);
            Assert.Equal(content, actual);
        }
        finally
        {
            listener.Stop();
            RunWsl($"rm -f {wslSourcePath}");
            try { Directory.Delete(destDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }
}
