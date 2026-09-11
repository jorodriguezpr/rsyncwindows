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
using RsyncWindows.Core.Daemon;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

public class DaemonHandshakeTests
{
    private static RsyncdConfig OneModuleConfig(bool readOnly = true) => RsyncdConfig.Parse(
    [
        "[stuff]",
        "path = C:\\somewhere",
        "comment = Test module",
        $"read only = {(readOnly ? "yes" : "no")}",
    ]);

    /// <summary>
    /// Full end-to-end coverage that a real client-side caller would do: RunServerSide +
    /// RunClientSide, THEN both sides feed their respective learned version into
    /// ProtocolNegotiator.Negotiate's preNegotiatedVersion -- exactly the sequence
    /// Cli.ClientRunner's daemon push/pull paths perform. Regression test for a real bug: an
    /// earlier RunClientSide returned void (discarding the parsed greeting version), so the
    /// caller had nothing to pass as preNegotiatedVersion and the client ended up ALSO
    /// attempting setup_protocol()'s raw 4-byte version exchange after the daemon greeting had
    /// already settled it -- desyncing the very next read. Only surfaced when this project's own
    /// CLI actually pushed to its own daemon for the first time (every other daemon-mode test
    /// pairs one side with a REAL rsync binary, which doesn't have this gap on either end).
    /// </summary>
    [Fact]
    public async Task FullHandshakeThenProtocolNegotiation_BothSidesAgree_NoRawVersionDesync()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig(readOnly: false);
        var serverArgs = new[] { "--server", "-vte32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        int clientRemoteVersion = await clientTask;
        var request = await serverTask;
        Assert.NotNull(request);

        var serverNegotiateTask = Task.Run(() => ProtocolNegotiator.Negotiate(
            right, isServer: true, preNegotiatedVersion: request!.RemoteProtocolVersion));
        var clientNegotiateTask = Task.Run(() => ProtocolNegotiator.Negotiate(
            left, isServer: false, preNegotiatedVersion: clientRemoteVersion));

        var serverSession = await serverNegotiateTask;
        var clientSession = await clientNegotiateTask;

        Assert.Equal(serverSession.ProtocolVersion, clientSession.ProtocolVersion);
        Assert.Equal(serverSession.ChecksumSeed, clientSession.ChecksumSeed);
    }

    [Fact]
    public async Task PullRequest_SenderFlagPresent_ServerSeesAmSenderTrue()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig();
        var serverArgs = new[] { "--server", "--sender", "-vvrte32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() =>
            DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() =>
            DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.True(request!.AmSender);
        Assert.Equal("stuff", request.Module.Name);
        Assert.Equal(serverArgs, request.ServerArgs);
    }

    [Fact]
    public async Task CompressFlagInBundle_ServerDetectsCompressTrue()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig();
        // 'z' precedes the "e32.0fxCIvu" capability suffix, matching ServerInvocationBuilder's
        // own flag ordering (v* l u n o g D t p r c R z, then the suffix).
        var serverArgs = new[] { "--server", "--sender", "-vvrtze32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.True(request!.Compress);
    }

    [Fact]
    public async Task NoCompressFlagInBundle_ServerDetectsCompressFalse()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig();
        var serverArgs = new[] { "--server", "--sender", "-vvrte32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.False(request!.Compress);
    }

    [Fact]
    public async Task LetterZInCapabilitySuffix_NotMisdetectedAsCompress()
    {
        // The capability suffix "e32.0fxCIvu" never contains a literal 'z', but this guards the
        // scanning cutoff itself: a 'z' placed AFTER the 'e' (i.e. inside/after the suffix, which
        // real rsync and ServerInvocationBuilder never do -- 'z' always precedes 'e') must NOT be
        // detected, proving HasShortFlag's scan genuinely stops at 'e' rather than just happening
        // to not find 'z' in this particular suffix string.
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig();
        var serverArgs = new[] { "--server", "--sender", "-vvrte32.0fxCIvuz", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.False(request!.Compress);
    }

    [Fact]
    public async Task PushRequest_NoSenderFlag_ServerSeesAmSenderFalse_WhenModuleWritable()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig(readOnly: false);
        var serverArgs = new[] { "--server", "-vvrte32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.False(request!.AmSender);
    }

    [Fact]
    public async Task PushRequest_ToReadOnlyModule_HandshakeSucceeds_ReadOnlyFlagExposedForCallerToEnforce()
    {
        // "OK" (and therefore the argv list revealing push-vs-pull) is exchanged before the
        // daemon handshake can know the direction, so read-only enforcement can't happen inside
        // the handshake without breaking wire compatibility -- see RunServerSide's doc comment.
        // The handshake just hands back AmSender + Module.ReadOnly for the caller to act on
        // after protocol negotiation, matching how real rsync itself only discovers this later.
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig(readOnly: true);
        var serverArgs = new[] { "--server", "-vvrte32.0fxCIvu", ".", "stuff/" };

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", serverArgs));

        await clientTask;
        var request = await serverTask;

        Assert.NotNull(request);
        Assert.False(request!.AmSender);
        Assert.True(request.Module.ReadOnly);
    }

    [Fact]
    public async Task UnknownModule_ServerReturnsNull_ClientSeesError()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = OneModuleConfig();

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "doesnotexist", ["--server", ".", "doesnotexist/"]));

        var request = await serverTask;
        Assert.Null(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => clientTask);
    }

    [Fact]
    public async Task HostDenied_ServerReturnsNull()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = RsyncdConfig.Parse(
        [
            "[stuff]",
            "path = C:\\somewhere",
            "hosts allow = 10.0.0.0/8",
        ]);

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "stuff", ["--server", ".", "stuff/"]));

        var request = await serverTask;
        Assert.Null(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => clientTask);
    }

    [Fact]
    public async Task ModuleListing_ReturnsConfiguredModules()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var config = RsyncdConfig.Parse(
        [
            "[visible]",
            "path = C:\\a",
            "comment = shown",
            "[hidden]",
            "path = C:\\b",
            "list = no",
        ]);

        var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
        var clientTask = Task.Run(() => DaemonHandshake.RunClientListModules(left));

        var listing = await clientTask;
        var request = await serverTask;

        Assert.Null(request); // a listing request never reaches a transfer
        Assert.Contains(listing, l => l.StartsWith("visible"));
        Assert.DoesNotContain(listing, l => l.StartsWith("hidden"));
    }

    [Fact]
    public async Task Auth_CorrectCredentials_Succeeds()
    {
        string secretsFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(secretsFile, "alice:hunter2\n");

            var (left, right) = LoopbackDuplexTransport.CreatePair();
            var config = RsyncdConfig.Parse(
            [
                "[secure]",
                "path = C:\\secure",
                "read only = no",
                "auth users = alice",
                $"secrets file = {secretsFile}",
            ]);
            var serverArgs = new[] { "--server", ".", "secure/" };

            var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
            var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "secure", serverArgs, "alice", "hunter2"));

            await clientTask;
            var request = await serverTask;

            Assert.NotNull(request);
            Assert.Equal("alice", request!.AuthenticatedUser);
        }
        finally
        {
            File.Delete(secretsFile);
        }
    }

    [Fact]
    public async Task Auth_WrongPassword_Fails()
    {
        string secretsFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(secretsFile, "alice:hunter2\n");

            var (left, right) = LoopbackDuplexTransport.CreatePair();
            var config = RsyncdConfig.Parse(
            [
                "[secure]",
                "path = C:\\secure",
                "read only = no",
                "auth users = alice",
                $"secrets file = {secretsFile}",
            ]);

            var serverTask = Task.Run(() => DaemonHandshake.RunServerSide(right, config, IPAddress.Loopback));
            var clientTask = Task.Run(() => DaemonHandshake.RunClientSide(left, "secure", ["--server", ".", "secure/"], "alice", "wrongpass"));

            var request = await serverTask;
            Assert.Null(request);
            await Assert.ThrowsAsync<InvalidOperationException>(() => clientTask);
        }
        finally
        {
            File.Delete(secretsFile);
        }
    }
}
