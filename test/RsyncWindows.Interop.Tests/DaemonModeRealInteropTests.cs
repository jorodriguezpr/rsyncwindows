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
using System.Text;
using RsyncWindows.Core.Daemon;
using RsyncWindows.Service;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Interop.Tests;

/// <summary>
/// Real interop for Phase 5 (daemon mode): the actual rsync 3.5.0 binary (built under WSL2,
/// same as Phases 4a/4b) talks genuine TCP daemon protocol -- `rsync://host:port/module/path`,
/// no `--rsh` relay this time -- straight to our own <see cref="TcpDaemonServerTransport"/> +
/// <see cref="DaemonHandshake"/> + <see cref="DaemonConnectionHandler"/> stack. Both roles are
/// exercised: the real client pulling from a read-only module (proves our
/// DaemonHandshake-then-SenderSession path, and FileListBuilder against a real directory) and
/// pushing to a writable module (proves the same daemon preamble in front of the
/// ReceiverSession path Phase 4b already validated over the `--rsh` relay).
///
/// Requires the same WSL2 + built rsync-3.5.0 binary as Phases 4a/4b, and the inbound Windows
/// Firewall rule for WSL2->Windows TCP already set up for that phase (see
/// project_rsyncwindows_phase4b_real_client_to_our_server memory) -- reused here since it's the
/// same network path, just a different port. Skipped automatically if the WSL2 binary isn't
/// reachable, matching the opt-in/manual pattern the other interop tests use.
/// </summary>
public class DaemonModeRealInteropTests
{
    private const string RsyncBinaryPath = "/home/jorodriguez/rsync-build/rsync";

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

    private static (TcpDaemonServerTransport transport, int port) StartDaemon(RsyncdConfig config)
    {
        var handler = new DaemonConnectionHandler(config, msg => Console.WriteLine("[daemon] " + msg));
        var endpoint = new IPEndPoint(IPAddress.Any, 0); // let the OS pick a free port
        var transport = new TcpDaemonServerTransport(endpoint, handler.Handle, msg => Console.WriteLine("[daemon-warn] " + msg));
        transport.Start();
        return (transport, transport.LocalEndpoint.Port);
    }

    /// <summary>
    /// Was KNOWN FAILING for several sessions (tracked in project_rsyncwindows_phase5_daemon_mode
    /// memory) -- the real receiver reported "got transfer request in phase 2" right as our
    /// echoed file-transfer response should arrive. Root cause turned out to be the file-list's
    /// uid/gid field presence not consistently matching the session's actual preserve_uid/gid
    /// flags between sender and receiver (see FileListEncoder's preserveUid/preserveGid
    /// parameters and DaemonConnectionHandler's argv-derived flag detection) -- a byte-count
    /// mismatch there desyncs everything downstream, surfacing as this exact "phase 2" symptom
    /// far from the actual cause. Fixed; confirmed passing against the real rsync 3.5.0 binary.
    /// </summary>
    [Fact]
    public async Task RealRsyncClient_PullsFromReadOnlyModule_ViaGenuineDaemonProtocol()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-phase5-pull-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);
        byte[] content = Encoding.UTF8.GetBytes("Hello OUT of RsyncWindows via genuine daemon mode (Phase 5)!\n");
        string fileName = "greeting.txt";
        await File.WriteAllBytesAsync(Path.Combine(localModuleDir, fileName), content);

        var config = RsyncdConfig.Parse(
        [
            "[pullmod]",
            $"path = {localModuleDir}",
            "read only = yes",
        ]);
        var (transport, port) = StartDaemon(config);

        string wslDestDir = "/tmp/rsyncwin-phase5-pull-dest";
        try
        {
            RunWsl($"rm -rf {wslDestDir} && mkdir -p {wslDestDir}");

            string cmd = $"{RsyncBinaryPath} -v rsync://{windowsHostIp}:{port}/pullmod/ {wslDestDir}/";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);
            Console.WriteLine("[test] dest dir listing: " + RunWslCapture($"ls -la {wslDestDir}/"));

            string actual = RunWslCapture($"cat {wslDestDir}/{fileName}");
            Assert.Equal(Encoding.UTF8.GetString(content), actual);
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -rf {wslDestDir}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }

    [Fact]
    public async Task RealRsyncClient_PushesToWritableModule_ViaGenuineDaemonProtocol()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-phase5-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);

        var config = RsyncdConfig.Parse(
        [
            "[pushmod]",
            $"path = {localModuleDir}",
            "read only = no",
        ]);
        var (transport, port) = StartDaemon(config);

        byte[] content = Encoding.UTF8.GetBytes("Hello INTO RsyncWindows via genuine daemon mode (Phase 5)!\n");
        string fileName = "phase5-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        string wslSourcePath = "/tmp/" + fileName;
        try
        {
            RunWsl($"printf '%s' '{Encoding.UTF8.GetString(content)}' > {wslSourcePath}");

            string cmd = $"{RsyncBinaryPath} -v {wslSourcePath} rsync://{windowsHostIp}:{port}/pushmod/{fileName}";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);

            string landed = Path.Combine(localModuleDir, fileName);
            Assert.True(File.Exists(landed), $"expected file did not land at {landed}");
            byte[] actual = await File.ReadAllBytesAsync(landed);
            Assert.Equal(content, actual);
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -f {wslSourcePath}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }

    /// <summary>
    /// Real rsync's send_id_lists()/recv_id_list() (uidlist.c) -- a block a SENDER writes right
    /// after its file list, present whenever `-o`/`-g` is active and `--numeric-ids` is NOT
    /// (the default). Every other daemon-mode test here uses plain `-v` (no `-o`/`-g` at all),
    /// so this block was never exchanged in any of them -- it went completely unnoticed until a
    /// genuine rsync client pushed `-og`/`-a` to the actual LIVE installed daemon for the first
    /// time and desynced on the very first file ("sender echoed ndx N, expected 0"). This test
    /// exists specifically to keep `-o`/`-g` daemon-mode PUSH covered by the fast suite going
    /// forward (see <see cref="RsyncWindows.Core.FileList.IdListCodec"/> for the fix).
    /// </summary>
    [Fact]
    public async Task RealRsyncClient_PushesWithOwnerGroupPreservation_ViaGenuineDaemonProtocol()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-idlist-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);

        var config = RsyncdConfig.Parse(["[pushog]", $"path = {localModuleDir}", "read only = no"]);
        var (transport, port) = StartDaemon(config);

        string fileName = "idlist-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        string wslSourcePath = "/tmp/" + fileName;
        try
        {
            RunWsl($"printf 'owner and group preservation test' > {wslSourcePath}");

            string cmd = $"{RsyncBinaryPath} -vog {wslSourcePath} rsync://{windowsHostIp}:{port}/pushog/{fileName}";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);

            string landed = Path.Combine(localModuleDir, fileName);
            Assert.True(File.Exists(landed), $"expected file did not land at {landed} -- output was: {output}");
            Assert.Equal("owner and group preservation test", await File.ReadAllTextAsync(landed));
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -f {wslSourcePath}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }

    /// <summary>Pull-direction twin of <see cref="RealRsyncClient_PushesWithOwnerGroupPreservation_ViaGenuineDaemonProtocol"/>
    /// -- exercises <see cref="DaemonConnectionHandler.RunAsSender"/>'s <c>IdListCodec.WriteIdLists</c>
    /// call against a real rsync client's <c>recv_id_list</c>.</summary>
    [Fact]
    public async Task RealRsyncClient_PullsWithOwnerGroupPreservation_ViaGenuineDaemonProtocol()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-idlist-pull-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);
        byte[] content = Encoding.UTF8.GetBytes("owner and group preservation pull test\n");
        await File.WriteAllBytesAsync(Path.Combine(localModuleDir, "greeting.txt"), content);

        var config = RsyncdConfig.Parse(["[pullog]", $"path = {localModuleDir}", "read only = yes"]);
        var (transport, port) = StartDaemon(config);

        string wslDestDir = "/tmp/rsyncwin-idlist-pull-dest-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            RunWsl($"rm -rf {wslDestDir} && mkdir -p {wslDestDir}");

            string cmd = $"{RsyncBinaryPath} -vog rsync://{windowsHostIp}:{port}/pullog/ {wslDestDir}/";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);

            string actual = RunWslCapture($"cat {wslDestDir}/greeting.txt");
            Assert.Equal(Encoding.UTF8.GetString(content), actual);
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -rf {wslDestDir}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }

    /// <summary>
    /// "Bug 8" -- was open after two attempts (the plain "virtual trailing slash" approximation
    /// of f_name_cmp) still failed, moving the mismatch from ndx 3 to ndx 4. Root cause pinned
    /// down via `--debug=FLIST4 --list-only --no-inc-recursive` ground-truth captures against
    /// the real rsync 3.5.0 binary: <see cref="RsyncWindows.Core.FileList.RsyncFileOrder"/> now
    /// ports f_name_cmp's actual component-wise state machine (all files before all dirs at a
    /// given level, a dir's descendants grouped immediately after it), not an approximation of
    /// it -- see RsyncFileOrderGroundTruthTests.cs for the pinned vectors.
    /// </summary>
    [Fact]
    public async Task RealRsyncClient_PushesTreeWithDirectoryNotLastSibling_AllFilesLandCorrectly()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-bug8-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);

        var config = RsyncdConfig.Parse(["[bug8mod]", $"path = {localModuleDir}", "read only = no"]);
        var (transport, port) = StartDaemon(config);

        string wslSrcDir = "/tmp/rsyncwin-bug8-src-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            RunWsl($"mkdir -p '{wslSrcDir}/gooddir/nested' && " +
                   $"printf 'before' > '{wslSrcDir}/aaa-before.txt' && " +
                   $"printf 'inside' > '{wslSrcDir}/gooddir/nested/inner.txt' && " +
                   $"printf 'after' > '{wslSrcDir}/zzz-after.txt'");

            string cmd = $"{RsyncBinaryPath} -rv {wslSrcDir}/ rsync://{windowsHostIp}:{port}/bug8mod/";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);

            Assert.True(File.Exists(Path.Combine(localModuleDir, "aaa-before.txt")));
            Assert.True(File.Exists(Path.Combine(localModuleDir, "zzz-after.txt")));
            Assert.True(File.Exists(Path.Combine(localModuleDir, "gooddir", "nested", "inner.txt")));
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(localModuleDir, "aaa-before.txt")));
            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(localModuleDir, "zzz-after.txt")));
            Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(localModuleDir, "gooddir", "nested", "inner.txt")));
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -rf {wslSrcDir}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }

    /// <summary>
    /// Real, live-discovered bug: a NAME that's legal on the sending Linux peer but illegal on
    /// NTFS (a colon, reserved for Alternate Data Streams) used to throw an unhandled exception
    /// out of <see cref="DaemonConnectionHandler.RunAsReceiver"/> (via `File.WriteAllBytes`/
    /// `Directory.CreateDirectory`), killing the ENTIRE connection -- every other file in the
    /// same push was lost too, not just the one bad entry. Confirmed live pushing a real project
    /// tree containing an accidental "C:" directory. Fixed via
    /// <see cref="RsyncWindows.Core.FileList.FileWriteSupport"/>.
    ///
    /// This test deliberately pushes three EXPLICIT FILE ARGUMENTS with no `-r` and no
    /// directories involved at all (not a recursive directory push) -- while chasing this bug, a
    /// SEPARATE, pre-existing, previously totally untested issue was found: a push whose file
    /// list mixes DIRECTORY entries with file entries desyncs against a real rsync client
    /// ("received request to transfer non-regular file: N"), regardless of naming, regardless of
    /// nesting depth, even for an EMPTY directory. That is a materially different, unsolved bug
    /// (every prior real-interop test in this project's history only ever pushed flat file lists
    /// with zero directory entries, so it was never exercised) -- tracked separately, NOT fixed
    /// by this change. Keeping this test file-args-only avoids that bug entirely, so it stays a
    /// clean, true regression test for the ACTUAL fix here (graceful per-entry skip on an
    /// NTFS-illegal name) without being blocked on the unrelated one.
    /// </summary>
    [Fact]
    public async Task RealRsyncClient_PushesFileWithIllegalWindowsName_OtherFilesStillLandSuccessfully()
    {
        if (!RealRsyncAvailable())
        {
            Console.WriteLine($"SKIPPED: real rsync binary not found at {RsyncBinaryPath} under WSL2");
            return;
        }

        string windowsHostIp = RunWslCapture("ip route show default | awk '{print $3}'").Trim();
        Assert.False(string.IsNullOrWhiteSpace(windowsHostIp));

        string localModuleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-badname-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localModuleDir);

        var config = RsyncdConfig.Parse(["[badname]", $"path = {localModuleDir}", "read only = no"]);
        var (transport, port) = StartDaemon(config);

        string wslSrcDir = "/tmp/rsyncwin-badname-src-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // A colon mid-filename is legal on ext4, illegal on NTFS (reserved for Alternate
            // Data Streams) -- the same character class that broke the real "C:" directory.
            RunWsl($"mkdir -p '{wslSrcDir}' && " +
                   $"printf 'before' > '{wslSrcDir}/aaa-before.txt' && " +
                   $"printf 'bad' > '{wslSrcDir}/bad:file.txt' && " +
                   $"printf 'after' > '{wslSrcDir}/zzz-after.txt'");

            string cmd = $"{RsyncBinaryPath} -v {wslSrcDir}/aaa-before.txt {wslSrcDir}/bad:file.txt {wslSrcDir}/zzz-after.txt rsync://{windowsHostIp}:{port}/badname/";
            Console.WriteLine("[test] running: " + cmd);
            string output = RunWslCapture(cmd, timeoutMs: 15_000);
            Console.WriteLine("[rsync client stdout] " + output);

            Assert.True(File.Exists(Path.Combine(localModuleDir, "aaa-before.txt")), "file before the bad name should have landed");
            Assert.True(File.Exists(Path.Combine(localModuleDir, "zzz-after.txt")), "file after the bad name should have landed");
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(localModuleDir, "aaa-before.txt")));
            Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(localModuleDir, "zzz-after.txt")));
        }
        finally
        {
            await transport.StopAsync();
            transport.Dispose();
            RunWsl($"rm -rf {wslSrcDir}");
            Directory.Delete(localModuleDir, recursive: true);
        }
    }
}
