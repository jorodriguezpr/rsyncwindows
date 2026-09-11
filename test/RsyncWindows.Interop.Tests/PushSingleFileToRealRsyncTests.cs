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
using System.Net.Sockets;
using System.Text;
using RsyncWindows.Core.Delta;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Options;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using Xunit;

namespace RsyncWindows.Interop.Tests;

/// <summary>
/// Real interop: our own client code pushes a file to the ACTUAL rsync 3.5.0 binary (built
/// from the reference source under WSL2). Original design spawned `wsl.exe -e &lt;rsync&gt;
/// --server ...` directly as a child process (mirroring real rsync's own `ssh host rsync
/// --server ...` invocation) -- but `wsl.exe`'s own stdio relay was found to CORRUPT binary
/// data when invoked as a .NET child process on this machine (confirmed by writing a known
/// 4-byte value and observing the real rsync binary receive something else and reject it
/// with "protocol version mismatch"; a byte-identical native-WSL probe using the same
/// argv worked instantly). Rather than fight that relay, this test instead connects over a
/// plain TCP socket to a small Python bridge running inside WSL2 (see the accompanying
/// tools/interop_tcp_bridge.py) that accepts a connection and spawns the same `rsync --server`
/// invocation with its stdio wired to that socket -- a socket has no such translation layer,
/// and this is functionally equivalent to how a real TCP daemon-mode connection works, which
/// this project needs anyway for Phase 5. The wire-level negotiation and transfer code under
/// test (ProtocolNegotiator, FileListEncoder, SenderSession, DeltaSender) is unchanged either
/// way -- only the transport differs from a real `ssh`/production invocation.
///
/// This is Phase 4a (our client -&gt; real server) per the plan -- the harder 4b direction (a
/// real rsync client invoking OUR --server) needs OpenSSH Server enabled on Windows and isn't
/// covered here. Requires: WSL2 Ubuntu with the reference rsync source built at
/// ~/rsync-build/rsync, and tools/interop_tcp_bridge.py running and listening on
/// <see cref="BridgePort"/> (see this session's setup steps) -- skipped automatically if the
/// port isn't reachable, since this is opt-in/manual per the plan (not part of the normal
/// `dotnet test` gate).
/// </summary>
public class PushSingleFileToRealRsyncTests
{
    private const int BridgePort = 17900;
    private const string RemoteDestDir = "/tmp/rsyncwin-dest"; // fixed -- baked into the running bridge's argv

    private static bool BridgeReachable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", BridgePort).Wait(2000) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string RunWslCapture(string bashCommand)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(bashCommand);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return output;
    }

    private static void RunWsl(string bashCommand) => RunWslCapture(bashCommand);

    /// <summary>Opens a fresh connection to the bridge (one rsync --server process per TCP
    /// connection), negotiates, and pushes one file under <paramref name="remoteName"/>.
    /// Returns the negotiated session's compat flags for callers that want to assert on them.</summary>
    private static CompatFlags PushOneFile(string remoteName, byte[] content)
    {
        var entry = new FileEntry
        {
            Path = remoteName,
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = content.Length,
            ModTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        using var client = new TcpClient();
        client.Connect("127.0.0.1", BridgePort);
        var stream = client.GetStream();
        var raw = new DuplexStreamPair(stream, stream);

        var session = ProtocolNegotiator.Negotiate(raw, isServer: false);

        // Plain `-v` server invocation (the bridge's fixed --server argv) carries no
        // -o/-g, so the flist must NOT contain uid/gid varints -- writing them desyncs
        // the real receiver's phase bookkeeping (see FileListEncoder's doc comment).
        var flistEncoder = new FileListEncoder();
        flistEncoder.Write(session.Output, entry, preserveUid: false, preserveGid: false);
        flistEncoder.WriteEndOfList(session.Output);
        session.Output.Flush();

        var files = new List<SenderSession.FileToSend> { new(entry, content) };
        SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed);
        session.Output.Flush();
        client.Close();

        return session.CompatFlags;
    }

    private static string Md5Hex(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data));

    // A second bridge, started manually for this session pointed at a real rsync `--server`
    // invocation that includes `-z` in its bundled flags (`--server -ze32.0fxCIvu . <dest>`) --
    // see PushOneFileCompressed's own opt-in check.
    private const int CompressedBridgePort = 17902;
    private const string CompressedRemoteDestDir = "/tmp/rsyncwin-dest-z";

    private static bool CompressedBridgeReachable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", CompressedBridgePort).Wait(2000) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void PushOneFileCompressed(string remoteName, byte[] content)
    {
        var entry = new FileEntry
        {
            Path = remoteName,
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = content.Length,
            ModTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        using var client = new TcpClient();
        client.Connect("127.0.0.1", CompressedBridgePort);
        var stream = client.GetStream();
        var raw = new DuplexStreamPair(stream, stream);

        var session = ProtocolNegotiator.Negotiate(raw, isServer: false, compress: true);
        Console.WriteLine($"[test] negotiated CompressionEnabled={session.CompressionEnabled}");

        // Same preserve-flag rule as PushOneFile: the -z bridge's --server argv has no
        // -o/-g, so the flist must not carry uid/gid varints.
        var flistEncoder = new FileListEncoder();
        flistEncoder.Write(session.Output, entry, preserveUid: false, preserveGid: false);
        flistEncoder.WriteEndOfList(session.Output);
        session.Output.Flush();

        var files = new List<SenderSession.FileToSend> { new(entry, content) };
        SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed, compress: session.CompressionEnabled);
        session.Output.Flush();
        client.Close();
    }

    /// <summary>
    /// Phase 6 real interop, the other direction from
    /// <see cref="RealClientPushesToOurServerTests.RealRsyncClient_PushesCompressedFile_ToOurServer_ReconstructsCorrectly"/>:
    /// OUR <see cref="RsyncWindows.Core.Delta.CompressedTokenWriter"/> (real-zlib-backed, see
    /// its doc comment) pushes a `-z`-compressed file to a REAL rsync server. Requires a SECOND
    /// bridge instance running with `-z` baked into its fixed `--server` invocation flags
    /// (started manually this session: `python3 tools/interop_tcp_bridge.py 17902 &lt;rsync&gt;
    /// --server -ze32.0fxCIvu . /tmp/rsyncwin-dest-z`) -- skipped automatically if that port
    /// isn't reachable, same opt-in pattern as <see cref="PushSingleFile_ToRealRsyncServer_LandsWithCorrectContent"/>.
    ///
    /// Was KNOWN FAILING for a session (a "got transfer request in phase 2" desync, same symptom
    /// text as the unrelated Phase 5 daemon-PULL bug but a different code path) -- root cause
    /// turned out to be the same class of bug as that one: the file list's uid/gid field
    /// presence not consistently matching the session's actual preserve_uid/gid flags between
    /// sender and receiver, a byte-count mismatch that desyncs everything downstream and
    /// surfaces as this exact symptom far from the real cause. Fixed (see the
    /// `preserveUid`/`preserveGid` plumbing in `PushOneFileCompressed` above); confirmed passing
    /// against the real rsync 3.5.0 binary.
    /// </summary>
    [Fact]
    public void PushCompressedFile_ToRealRsyncServer_LandsWithCorrectContent()
    {
        if (!CompressedBridgeReachable())
        {
            Console.WriteLine($"SKIPPED: no TCP bridge reachable on port {CompressedBridgePort} -- see class doc comment for setup");
            return;
        }

        RunWsl($"rm -rf {CompressedRemoteDestDir} && mkdir -p {CompressedRemoteDestDir}");

        var textBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
            textBuilder.AppendLine($"Line {i}: the quick brown fox jumps over the lazy dog {i * 7}.");
        byte[] content = Encoding.UTF8.GetBytes(textBuilder.ToString());

        PushOneFileCompressed("hello-z.txt", content);

        string actual = RunWslCapture($"cat {CompressedRemoteDestDir}/hello-z.txt");
        Assert.Equal(Encoding.UTF8.GetString(content), actual);

        string actualMd5 = RunWslCapture($"md5sum {CompressedRemoteDestDir}/hello-z.txt").Split(' ')[0];
        Assert.Equal(Md5Hex(content), actualMd5);
    }

    [Fact]
    public void PushSingleFile_ToRealRsyncServer_LandsWithCorrectContent()
    {
        // Opt-in/manual per the plan -- not part of the normal `dotnet test` gate. A plain
        // early-return (rather than a Skip package dependency) keeps this project dependency-free.
        if (!BridgeReachable())
        {
            Console.WriteLine($"SKIPPED: no TCP bridge reachable on port {BridgePort} -- see class doc comment for setup");
            return;
        }

        RunWsl($"rm -rf {RemoteDestDir} && mkdir -p {RemoteDestDir}");

        byte[] content = Encoding.UTF8.GetBytes("Hello from RsyncWindows Phase 4a interop test!\n");

        var flags = PushOneFile("hello.txt", content);

        // Confirm our client_info suffix (see ServerInvocationBuilder) actually persuaded the
        // real server to enable the capabilities this v1's wire code hard-depends on.
        Assert.True(flags.HasFlag(CompatFlags.VarintFlistFlags),
            "real server did not enable CF_VARINT_FLIST_FLAGS -- our client_info suffix was not understood as intended");
        Assert.True(flags.HasFlag(CompatFlags.ChecksumSeedFix),
            "real server did not enable CF_CHKSUM_SEED_FIX -- checksum seed ordering would silently mismatch");

        string actual = RunWslCapture($"cat {RemoteDestDir}/hello.txt");
        Assert.Equal(Encoding.UTF8.GetString(content), actual);

        string actualMd5 = RunWslCapture($"md5sum {RemoteDestDir}/hello.txt").Split(' ')[0];
        Assert.Equal(Md5Hex(content), actualMd5);
    }

    [Fact]
    public void PushModifiedFile_TriggersRealDeltaMatching_AgainstExistingDestination()
    {
        // This is the test the plan's "scope note" flagged as missing: Phase 4a's first test
        // only ever exercises an EMPTY basis file (new destination, sum_head all-zero, pure
        // literal transfer) -- DeltaSender's actual block-matching logic (hash table, rolling
        // search, strong-checksum verification) was only proven against our own in-memory
        // round-trip tests, never against a checksum table a REAL generator computed. This
        // test pushes a multi-block file, then pushes a MODIFIED version of the same file to
        // the same destination -- the second push forces the real generator to send a real,
        // non-empty checksum table, and our DeltaSender has to correctly match blocks against it.
        if (!BridgeReachable())
        {
            Console.WriteLine($"SKIPPED: no TCP bridge reachable on port {BridgePort} -- see class doc comment for setup");
            return;
        }

        RunWsl($"rm -rf {RemoteDestDir} && mkdir -p {RemoteDestDir}");

        // Large enough to span several blocks (BlockSizer floors at 700 bytes/block).
        var rnd = new Random(20260907);
        byte[] original = new byte[20_000];
        rnd.NextBytes(original);

        PushOneFile("data.bin", original);
        string firstMd5 = RunWslCapture($"md5sum {RemoteDestDir}/data.bin").Split(' ')[0];
        Assert.Equal(Md5Hex(original), firstMd5);

        // Modify: change a chunk in the middle, insert some bytes, leave the rest untouched --
        // a real-world "small edit" pattern that should produce a mix of matched blocks and
        // literal runs, not an all-literal or all-matched degenerate case.
        var modified = new List<byte>(original[..8000]);
        var inserted = new byte[500];
        rnd.NextBytes(inserted);
        modified.AddRange(inserted);
        modified.AddRange(original[8000..15000]);
        for (int i = 0; i < 300; i++)
            modified[9000 + i] ^= 0xFF; // byte-flip a run within the untouched-looking region
        modified.AddRange(original[15000..]);
        byte[] modifiedBytes = modified.ToArray();

        PushOneFile("data.bin", modifiedBytes);

        string actualMd5 = RunWslCapture($"md5sum {RemoteDestDir}/data.bin").Split(' ')[0];
        Assert.Equal(Md5Hex(modifiedBytes), actualMd5);

        string sizeStr = RunWslCapture($"stat -c %s {RemoteDestDir}/data.bin").Trim();
        Assert.Equal(modifiedBytes.Length, int.Parse(sizeStr));
    }

    [Fact]
    public void PushModifiedFileCompressed_TriggersRealDeltaMatching_WithZActive()
    {
        // The Phase 6 scope-note gap: every earlier compression interop test pushed to an
        // EMPTY destination, so the real generator's checksum table was all-zero and our
        // compressed token stream was all-literal. This pairs the two -- second push of a
        // modified file over an existing basis, with `-z` active on both sides, forcing the
        // real generator to emit a real checksum table AND our CompressedTokenWriter to
        // emit matched-block tokens (with their history-priming) interleaved with
        // compressed literal runs. All four real-peer -z hazards live in this one path:
        // priming symmetry, run encoding, sync-flush trimming, and the s2length agreement.
        if (!CompressedBridgeReachable())
        {
            Console.WriteLine($"SKIPPED: no TCP bridge reachable on port {CompressedBridgePort} -- see class doc comment for setup");
            return;
        }

        RunWsl($"rm -rf {CompressedRemoteDestDir} && mkdir -p {CompressedRemoteDestDir}");

        var rnd = new Random(20260908);
        byte[] original = new byte[20_000];
        rnd.NextBytes(original);

        PushOneFileCompressed("data-z.bin", original);
        string firstMd5 = RunWslCapture($"md5sum {CompressedRemoteDestDir}/data-z.bin").Split(' ')[0];
        Assert.Equal(Md5Hex(original), firstMd5);

        var modified = new List<byte>(original[..8000]);
        var inserted = new byte[500];
        rnd.NextBytes(inserted);
        modified.AddRange(inserted);
        modified.AddRange(original[8000..15000]);
        for (int i = 0; i < 300; i++)
            modified[9000 + i] ^= 0xFF;
        modified.AddRange(original[15000..]);
        byte[] modifiedBytes = modified.ToArray();

        PushOneFileCompressed("data-z.bin", modifiedBytes);

        string actualMd5 = RunWslCapture($"md5sum {CompressedRemoteDestDir}/data-z.bin").Split(' ')[0];
        Assert.Equal(Md5Hex(modifiedBytes), actualMd5);

        string sizeStr = RunWslCapture($"stat -c %s {CompressedRemoteDestDir}/data-z.bin").Trim();
        Assert.Equal(modifiedBytes.Length, int.Parse(sizeStr));
    }
}
