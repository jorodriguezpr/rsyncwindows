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
/// Verifies the tray app's new "Manage Allowed Users" feature is actually usable end-to-end from
/// THIS project's own CLI, not just from a real rsync client -- <c>ClientRunner</c>'s
/// <c>RunDaemonPush</c>/<c>RunDaemonPull</c> never threaded a username/password through to
/// <see cref="DaemonHandshake.RunClientSide"/> before now (no test caught this because
/// <see cref="DaemonHandshakeTests"/> drives <c>RunClientSide</c> directly, and every other daemon
/// interop test here uses unprotected modules), so a module an admin protected via the tray's user
/// list was previously reachable only by a real rsync client's <c>--password-file</c>/URL-embedded
/// user, never by <c>rsyncWindows.exe</c> itself. Fixed by resolving credentials from the URL's
/// <c>user@host</c> part and the newly-added <c>--password-file</c> option
/// (<c>ClientRunner.ResolveDaemonCredentials</c>) and passing them through.
/// </summary>
public class RealCliExeDaemonAuthTests
{
    private const string CliExePath = @"C:\PhpProjects\rsyncWindows\dist\bin\cli\rsyncWindows.exe";

    private sealed record PushResult(bool Exited, int ExitCode, string Stdout, string Stderr, byte[]? LandedContent);

    private static async Task<PushResult> PushToProtectedModule(string? passwordFileContent, string urlUser)
    {
        string moduleDir = Path.Combine(Path.GetTempPath(), "rsyncwin-authtest-mod-" + Guid.NewGuid().ToString("N")[..8]);
        string srcDir = Path.Combine(Path.GetTempPath(), "rsyncwin-authtest-src-" + Guid.NewGuid().ToString("N")[..8]);
        string secretsPath = Path.Combine(Path.GetTempPath(), "rsyncwin-authtest-secrets-" + Guid.NewGuid().ToString("N")[..8] + ".secrets");
        Directory.CreateDirectory(moduleDir);
        Directory.CreateDirectory(srcDir);
        byte[] content = "protected content"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "secret.txt"), content);
        RsyncdSecretsFile.Save(secretsPath, [new RsyncdSecretsFile.Entry("alice", "correct-horse-battery-staple")]);

        var config = RsyncdConfig.Parse(["[protectedmod]", $"path = {moduleDir}", "read only = no", "auth users = alice", $"secrets file = {secretsPath}"]);
        var errors = new List<string>();

        void HandleConnection(DuplexStreamPair raw, IPAddress clientAddress)
        {
            try
            {
                var request = DaemonHandshake.RunServerSide(raw, config, clientAddress);
                if (request == null)
                    return; // auth failed / denied -- exactly what the "wrong password" case expects
                var serverSession = ProtocolNegotiator.Negotiate(raw, isServer: true, preNegotiatedVersion: request.RemoteProtocolVersion, compress: request.Compress);
                // -av implies -o/-g on the client's (sender's) side of this push, so its flist
                // encoding includes uid/gid fields -- the receiver here must decode the same
                // fields or desync on the first entry (the exact mechanism behind this project's
                // earlier "Bug E", see project memory).
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

        string? passwordFilePath = null;
        try
        {
            if (passwordFileContent != null)
            {
                passwordFilePath = Path.Combine(Path.GetTempPath(), "rsyncwin-authtest-pwfile-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
                await File.WriteAllTextAsync(passwordFilePath, passwordFileContent);
            }

            var psi = new ProcessStartInfo(CliExePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-av");
            if (passwordFilePath != null)
            {
                psi.ArgumentList.Add("--password-file");
                psi.ArgumentList.Add(passwordFilePath);
            }
            psi.ArgumentList.Add(srcDir + "\\");
            psi.ArgumentList.Add($"rsync://{urlUser}127.0.0.1:{port}/protectedmod/");

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

            string landedPath = Path.Combine(moduleDir, "secret.txt");
            byte[]? landed = File.Exists(landedPath) ? await File.ReadAllBytesAsync(landedPath) : null;
            return new PushResult(exited, exited ? proc.ExitCode : -1, stdout, stderr, landed);
        }
        finally
        {
            await serverTransport.StopAsync();
            Directory.Delete(moduleDir, recursive: true);
            Directory.Delete(srcDir, recursive: true);
            if (passwordFilePath != null)
                File.Delete(passwordFilePath);
            File.Delete(secretsPath);
        }
    }

    [Fact]
    public async Task RealPublishedExe_CorrectPasswordFile_PushesSuccessfully()
    {
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: {CliExePath} not found -- publish the Cli project first");
            return;
        }

        var result = await PushToProtectedModule("correct-horse-battery-staple", "alice@");

        Console.WriteLine($"exited: {result.Exited}, exit code: {result.ExitCode}");
        Console.WriteLine($"stdout: {result.Stdout}");
        Console.WriteLine($"stderr: {result.Stderr}");

        Assert.True(result.Exited, "rsyncWindows.exe did not exit within 10s (hung)");
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.LandedContent);
        Assert.Equal("protected content"u8.ToArray(), result.LandedContent);
    }

    [Fact]
    public async Task RealPublishedExe_WrongPasswordFile_FailsAndLandsNothing()
    {
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: {CliExePath} not found -- publish the Cli project first");
            return;
        }

        var result = await PushToProtectedModule("totally-wrong-password", "alice@");

        Console.WriteLine($"exited: {result.Exited}, exit code: {result.ExitCode}");
        Console.WriteLine($"stderr: {result.Stderr}");

        Assert.True(result.Exited, "rsyncWindows.exe did not exit within 10s (hung)");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(result.LandedContent);
    }

    [Fact]
    public async Task RealPublishedExe_NoCredentialsAtAll_FailsAndLandsNothing()
    {
        if (!File.Exists(CliExePath))
        {
            Console.WriteLine($"SKIPPED: {CliExePath} not found -- publish the Cli project first");
            return;
        }

        var result = await PushToProtectedModule(passwordFileContent: null, urlUser: "");

        Assert.True(result.Exited, "rsyncWindows.exe did not exit within 10s (hung)");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(result.LandedContent);
    }
}
