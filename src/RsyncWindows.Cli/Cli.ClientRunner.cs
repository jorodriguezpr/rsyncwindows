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

// ClientRunner: everything after argv parsing for a client invocation.
// Entry point (Program/Main) lives in RsyncWindows.Cli.Program.cs; the --server role in Cli.ServerRole.cs.

using System.Text;
using RsyncWindows.Core.Daemon;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Options;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;

namespace RsyncWindows.Cli;

internal sealed class ClientRunner(RsyncOptions options)
{
    private readonly FilterEngine _filters = BuildFilters(options);

    // Files/entries skipped because of a per-file error (access denied, locked, illegal name...).
    // Like real rsync, the run continues and finishes with exit code 23 ("partial transfer due
    // to error") instead of aborting everything over one file.
    private int _skipped;

    private void Skip(string what, string? error)
    {
        _skipped++;
        Console.Error.WriteLine($"rsyncWindows: skipped {what}: {error}");
    }

    private int ExitCode()
    {
        if (_skipped > 0)
            Console.Error.WriteLine($"rsyncWindows: {_skipped} item(s) skipped because of errors (see above); everything else was transferred");
        return _skipped > 0 ? 23 : 0;
    }

    public int Run()
    {
        var destSpec = RemoteSpec.Parse(options.Destination);
        if (destSpec is RemoteSpec.Local destLocal)
        {
            var remoteSource = options.Sources.Select(RemoteSpec.Parse).FirstOrDefault(s => s is not RemoteSpec.Local);
            if (remoteSource == null)
                return RunLocalToLocal(destLocal.Path);
            return remoteSource switch
            {
                RemoteSpec.Daemon daemonSrc => RunDaemonPull(daemonSrc),
                RemoteSpec.Ssh sshSrc => RunSshPull(sshSrc),
                _ => throw new RsyncArgException("unsupported remote source"),
            };
        }

        if (options.Sources.Any(s => RemoteSpec.Parse(s) is not RemoteSpec.Local))
            throw new RsyncArgException("v1 supports at most one remote endpoint: sources must be local when the destination is remote");

        return destSpec switch
        {
            RemoteSpec.Daemon daemon => RunDaemonPush(daemon),
            RemoteSpec.Ssh ssh => RunSshPush(ssh),
            _ => throw new RsyncArgException("unsupported destination"),
        };
    }

    // ------------------------------------------------------------- local -> local

    private int RunLocalToLocal(string destPath)
    {
        Directory.CreateDirectory(destPath);
        var items = EnumerateSources().ToList();
        // Doesn't go through ReceiverSession (which checks this internally for the SSH/daemon
        // paths) -- local-to-local writes straight to a case-insensitive-but-preserving Windows
        // filesystem too, so the same check applies here explicitly.
        Core.FileList.CaseCollisionDetector.ThrowIfCollisions(items.Select(i => i.Entry).ToList());

        int transferred = 0;
        foreach (var item in items)
        {
            Report(item.WirePath);
            if (options.DryRun)
            {
                transferred++;
                continue;
            }
            string target = ResolveLocal(destPath, item.WirePath);
            if (item.Entry.FileType == RsyncFileType.Directory)
            {
                if (!FileWriteSupport.TryCreateDirectory(target, out string? dirError))
                    Skip($"directory '{item.WirePath}'", dirError);
                continue;
            }
            if (item.Entry.FileType == RsyncFileType.Symlink)
            {
                if (item.Entry.SymlinkTarget != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!Core.FileList.SymlinkSupport.TryCreateSymlink(target, item.Entry.SymlinkTarget, out string? error))
                        Skip($"symlink '{item.WirePath}'", error);
                }
                continue;
            }
            if (item.Entry.FileType != RsyncFileType.Regular)
                continue;

            if (!FileWriteSupport.TryCopyFile(item.LocalPath, target, out string? copyError))
            {
                Skip($"'{item.WirePath}'", copyError);
                continue;
            }
            TrySetTimes(target, item.Entry);
            TrySetPermissions(target, item.Entry);
            transferred++;
        }
        Report($"sent {transferred} file(s)");
        return ExitCode();
    }

    // ------------------------------------------------------------- push (SSH / daemon)

    /// <summary>Splits a -e/--rsh value into its argv tokens (real rsync's shell_cmd
    /// handling: the rsh string is a whitespace-separated command line, e.g.
    /// "python3 tools/interop_rsh_connector.py host port" is FOUR tokens -- the first is the
    /// program, the rest are its arguments). The earlier version treated the whole string as
    /// one program name, which only ever worked for a bare "ssh".</summary>
    private static (string Program, IReadOnlyList<string> Args) SplitRsh(string? rsh)
    {
        if (string.IsNullOrWhiteSpace(rsh))
            return ("ssh", []);
        var tokens = rsh.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (tokens[0], tokens.Skip(1).ToList());
    }

    private int RunSshPush(RemoteSpec.Ssh dest)
    {
        // amSender = "is the LOCAL side the sender" -- true here: we're pushing, so we send and
        // the remote --server process must NOT get --sender (it's the receiver). Real rsync's
        // own do_cmd()/server_options(): the remote is told --sender exactly when the LOCAL side
        // is NOT the sender (a pull). Getting this backwards makes both ends think they're the
        // same role, and neither the sender nor receiver protocol logic runs correctly on either
        // side -- confirmed as a real bug (all four call sites in this file had it inverted)
        // once this project's own client-initiated push/pull paths were actually exercised for
        // the first time (every prior interop test either drove SenderSession/ReceiverSession
        // directly, bypassing ClientRunner, or exercised the --server RESPONDER role, never the
        // INITIATING role built here).
        var serverArgs = ServerInvocationBuilder.BuildServerArgs(options, amSender: true, dest.Path);
        var (shellProgram, shellArgs) = SplitRsh(options.RemoteShell);

        // Real rsync's do_cmd(): `<rsh tokens> [user@]host rsync --server <flags> . <path>`
        var spawn = new List<string>(shellArgs) { SshHostArg(dest) };
        spawn.Add(options.RsyncPath ?? "rsync");
        spawn.AddRange(serverArgs);

        using var transport = new SshProcessTransport(shellProgram, spawn);
        var raw = transport.Connect();
        var session = ProtocolNegotiator.Negotiate(raw, isServer: false, compress: options.Compress);
        PushSources(session);
        return ExitCode();
    }

    private int RunDaemonPush(RemoteSpec.Daemon dest)
    {
        int port = dest.Port ?? TcpDaemonClientTransport.DefaultPort;
        using var client = new TcpDaemonClientTransport(dest.Host, port);
        var raw = client.Connect();

        // amSender: true -- see RunSshPush's comment for the polarity (this is also a push).
        var serverArgs = ServerInvocationBuilder.BuildServerArgs(options, amSender: true, dest.Path);
        var (username, password) = ResolveDaemonCredentials(dest.User);
        int remoteVersion = DaemonHandshake.RunClientSide(raw, dest.Module, serverArgs, username, password);

        // Daemon mode negotiates the protocol version once, in the @RSYNCD: greeting line just
        // read above -- preNegotiatedVersion skips setup_protocol()'s raw 4-byte version exchange
        // a SECOND time, which would otherwise desync the very next read (see RunClientSide's own
        // doc comment for how this surfaced: "protocol version mismatch -- remote sent <garbage>").
        var session = ProtocolNegotiator.Negotiate(raw, isServer: false, preNegotiatedVersion: remoteVersion, compress: options.Compress);
        PushSources(session);
        return ExitCode();
    }

    // ------------------------------------------------------------- pull (SSH / daemon)

    private int RunSshPull(RemoteSpec.Ssh src)
    {
        // amSender: false -- we're pulling, so the LOCAL side is the receiver and the remote
        // --server process MUST get --sender. See RunSshPush's comment for the full polarity
        // explanation (this was inverted here too before being fixed).
        var serverArgs = ServerInvocationBuilder.BuildServerArgs(options, amSender: false, src.Path);
        var (shellProgram, shellArgs) = SplitRsh(options.RemoteShell);

        var spawn = new List<string>(shellArgs) { SshHostArg(src) };
        spawn.Add(options.RsyncPath ?? "rsync");
        spawn.AddRange(serverArgs);

        using var transport = new SshProcessTransport(shellProgram, spawn);
        var raw = transport.Connect();
        var session = ProtocolNegotiator.Negotiate(raw, isServer: false, compress: options.Compress);
        PullInto(session);
        return ExitCode();
    }

    private int RunDaemonPull(RemoteSpec.Daemon src)
    {
        int port = src.Port ?? TcpDaemonClientTransport.DefaultPort;
        using var client = new TcpDaemonClientTransport(src.Host, port);
        var raw = client.Connect();

        // amSender: false -- see RunSshPull's comment for the polarity (this is also a pull).
        var serverArgs = ServerInvocationBuilder.BuildServerArgs(options, amSender: false, src.Path);
        var (username, password) = ResolveDaemonCredentials(src.User);
        int remoteVersion = DaemonHandshake.RunClientSide(raw, src.Module, serverArgs, username, password);

        var session = ProtocolNegotiator.Negotiate(raw, isServer: false, preNegotiatedVersion: remoteVersion, compress: options.Compress);
        PullInto(session);
        return ExitCode();
    }

    /// <summary>Resolves the username/password <see cref="DaemonHandshake.RunClientSide"/> should
    /// answer an AUTHREQD challenge with, for a module that turns out to require auth (an
    /// unprotected module ignores both -- see RunClientSide's own no-challenge path). Username:
    /// the URL's <c>user@host</c> part if given (`rsync://alice@host/module`), else the local OS
    /// username -- matching real rsync's own clientserver.c default (it never silently sends
    /// "nobody" unless a challenge shows up with truly nothing else to offer). Password: the
    /// first line of <c>--password-file</c> if given, else the <c>RSYNC_PASSWORD</c> environment
    /// variable (both real rsync's own documented daemon-auth mechanisms -- rsync(1)), else null
    /// (RunClientSide treats that as an empty-string secret, which only succeeds against a module
    /// whose secrets file genuinely has an empty password for that user).</summary>
    private (string username, string? password) ResolveDaemonCredentials(string? urlUser)
    {
        string username = string.IsNullOrEmpty(urlUser) ? Environment.UserName : urlUser;

        string? password = null;
        if (!string.IsNullOrEmpty(options.PasswordFile))
        {
            string[] lines = File.ReadAllLines(options.PasswordFile);
            password = lines.Length > 0 ? lines[0] : "";
        }
        password ??= Environment.GetEnvironmentVariable("RSYNC_PASSWORD");

        return (username, password);
    }

    // ------------------------------------------------------------- shared paths

    private void PushSources(NegotiatedSession session)
    {
        var entries = new List<FileEntry>();
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var source in EnumerateSources())
        {
            if (!entries.Any(e => e.Path == source.WirePath))
            {
                if (source.Entry.FileType == RsyncFileType.Regular)
                {
                    // Read BEFORE the entry joins the flist: the peer requests files by flist
                    // index, so an unreadable file must never be announced in the first place.
                    if (!FileReadSupport.TryReadFile(source.LocalPath, out byte[] content, out string? readError))
                    {
                        Skip($"'{source.WirePath}' (cannot read source)", readError);
                        continue;
                    }
                    data[source.WirePath] = content;
                }
                entries.Add(source.Entry);
            }
        }

        // The peer's generator numbers its transfer requests by position in rsync's own GLOBAL
        // sort of the flattened flist, NOT the order sources were walked/enumerated here -- see
        // RsyncFileOrder's doc comment ("Bug 8": a real client's -og push to our own daemon
        // desynced the moment a directory wasn't the last sibling at its level, since walk order
        // and rsync's real ndx-numbering order are genuinely different things).
        entries = RsyncFileOrder.Sort(entries);

        // Mirror the flist's preserve flags on the wire: only send uid/gid when the session's
        // -o/-g said to -- a real peer's recv_file_entry (flist.c:1000/1011) only consumes
        // those fields under the matching preserve flags, and stray bytes there desync the
        // whole post-flist exchange (the root cause of the Phase 5/6 interop failures).
        bool preserveUid = options.PreserveOwner;
        bool preserveGid = options.PreserveGroup;

        var encoder = new FileListEncoder();
        foreach (var entry in entries)
            encoder.Write(session.Output, entry, preserveUid, preserveGid);
        encoder.WriteEndOfList(session.Output);
        IdListCodec.WriteIdLists(session.Output, entries, preserveUid, preserveGid, session.CompatFlags.HasFlag(CompatFlags.Id0Names));
        session.Output.Flush();

        // MUST be entries.Select(), NOT .Where(Regular).Select() -- the peer's generator
        // requests transfers by the entry's POSITION in the full flist we just sent (Directory/
        // Symlink entries included), so SenderSession's own files[ndx] indexing only lines up
        // when this list has exactly one FileToSend per flist entry, in the same order. Filtering
        // out non-regular entries here shifts every later index, corrupting any transfer whose
        // flist contains a directory or symlink before a file (i.e. almost any real tree).
        var files = entries
            .Select(e => new SenderSession.FileToSend(e, e.FileType == RsyncFileType.Regular && data.TryGetValue(e.Path, out var b) ? b : []))
            .ToList();
        SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed, compress: session.CompressionEnabled, onFileStart: e => Report(e.Path));
        session.Output.Flush();

        Report($"sent {files.Count(f => f.Entry.FileType == RsyncFileType.Regular)} file(s)");
    }

    private void PullInto(NegotiatedSession session)
    {
        string destRoot = options.Destination;
        Directory.CreateDirectory(destRoot);
        // Must match what the SENDER actually encoded its flist with, which it derives from
        // the SAME -o/-g flags we sent it via ServerInvocationBuilder (see PushSources' matching
        // comment) -- defaulting these to false regardless of options.PreserveOwner/Group (the
        // bug this fixes) desyncs FileListDecoder the moment the sender's flist genuinely
        // includes uid/gid fields (any `-a`/`-o`/`-g` pull), corrupting every read from the flist
        // decode onward -- confirmed directly against the real installed daemon: surfaced many
        // reads later as "sender echoed ndx -1, expected 0" on the very first file.
        var received = ReceiverSession.RunReceiverLoop(
            session.Input, session.Output,
            path => ReadLocalBasis(destRoot, path),
            session.ChecksumSeed,
            compress: session.CompressionEnabled,
            preserveUid: options.PreserveOwner,
            preserveGid: options.PreserveGroup,
            id0Names: session.CompatFlags.HasFlag(CompatFlags.Id0Names),
            onNonTransferEntry: e => ApplyNonTransferEntry(destRoot, e),
            onFileStart: e => Report(e.Path),
            sendFilterList: true);
        foreach (var file in received)
        {
            string target = ResolveLocal(destRoot, file.Entry.Path);
            if (!FileWriteSupport.TryWriteFile(target, file.Data, out string? writeError))
            {
                Skip($"'{file.Entry.Path}'", writeError);
                continue;
            }
            TrySetTimes(target, file.Entry);
            TrySetPermissions(target, file.Entry);
        }
        Report($"received {received.Count} file(s) into {destRoot}");
    }

    /// <summary>Applies a Directory or Symlink flist entry directly to disk during a pull -- no
    /// transfer request is ever made for these (a directory has no content of its own; a
    /// symlink's target string is already complete from the flist alone). Both directory and
    /// symlink creation degrade gracefully (warn to stderr, skip the entry) rather than aborting
    /// the whole transfer -- for symlinks, when the process lacks SeCreateSymbolicLinkPrivilege;
    /// for directories, when the peer's name is legal on ITS filesystem but not NTFS (e.g. a
    /// colon) -- see <see cref="FileWriteSupport"/>'s doc comment for why this matters: one bad
    /// name used to kill the entire connection, losing every other file in the same transfer.</summary>
    private void ApplyNonTransferEntry(string destRoot, FileEntry entry)
    {
        string full = ResolveLocal(destRoot, entry.Path);
        if (entry.FileType == RsyncFileType.Directory)
        {
            if (!FileWriteSupport.TryCreateDirectory(full, out string? error))
                Skip($"directory '{entry.Path}'", error);
        }
        else if (entry.FileType == RsyncFileType.Symlink && entry.SymlinkTarget != null)
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !FileWriteSupport.TryCreateDirectory(dir, out string? dirError))
            {
                Skip($"symlink '{entry.Path}'", dirError);
                return;
            }
            if (!Core.FileList.SymlinkSupport.TryCreateSymlink(full, entry.SymlinkTarget, out string? error))
                Skip($"symlink '{entry.Path}'", error);
        }
    }

    // ------------------------------------------------------------- enumeration

    private sealed record SourceItem(FileEntry Entry, string WirePath, string LocalPath);

    /// <summary>Walks every local source into (wire-entry, local-path) pairs. A trailing
    /// slash on a directory source means "the directory's CONTENTS are the transfer root"
    /// (names relative to src/); without it the leaf directory itself is the root -- matching
    /// real rsync's trailing-slash semantics.</summary>
    private IEnumerable<SourceItem> EnumerateSources()
    {
        foreach (var src in options.Sources)
        {
            string fullPath = Path.GetFullPath(src);
            bool slashSuffix = src.EndsWith('/') || src.EndsWith('\\');

            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                string wire = Path.GetFileName(src.Replace('\\', '/').TrimEnd('/'));
                var entry = new FileEntry
                {
                    Path = wire,
                    FileType = RsyncFileType.Regular,
                    Mode = UnixMode.RegularFileDefault,
                    Length = file.Length,
                    ModTimeUnix = ToUnixSeconds(file.LastWriteTimeUtc),
                    Uid = 0,
                    Gid = 0,
                };
                if (_filters.IsIncluded(entry.Path))
                    yield return new SourceItem(entry, entry.Path, fullPath);
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                foreach (var e in FileListBuilder.Walk(fullPath))
                {
                    string wire = slashSuffix
                        ? e.Path
                        : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)) + "/" + e.Path;
                    if (!_filters.IsIncluded(wire))
                        continue;
                    var prefixed = new FileEntry
                    {
                        Path = wire,
                        FileType = e.FileType,
                        Mode = e.Mode,
                        Length = e.Length,
                        ModTimeUnix = e.ModTimeUnix,
                        Uid = e.Uid,
                        Gid = e.Gid,
                        SymlinkTarget = e.SymlinkTarget,
                    };
                    yield return new SourceItem(prefixed, wire, ResolveLocal(fullPath, e.Path));
                }
                continue;
            }

            throw new FileNotFoundException($"source not found: {src}");
        }
    }

    // ------------------------------------------------------------- small helpers

    private static FilterEngine BuildFilters(RsyncOptions o)
    {
        var engine = new FilterEngine();
        foreach (var include in o.IncludePatterns)
            engine.AddInclude(include);
        foreach (var exclude in o.ExcludePatterns)
            engine.AddExclude(exclude);
        return engine;
    }

    /// <summary>The existing destination file as the delta basis. One that can't be read (access
    /// denied, locked) becomes an empty basis -- the sender transfers the whole file, as real rsync
    /// does -- instead of an exception that aborts the whole pull. The write that follows may still
    /// fail for the same reason; that one is skipped and reported per file.</summary>
    private static byte[] ReadLocalBasis(string destRoot, string wirePath)
    {
        string full = ResolveLocal(destRoot, wirePath);
        byte[] basis = FileReadSupport.ReadBasisOrEmpty(full, out string? error);
        if (error != null)
            Console.Error.WriteLine($"rsyncWindows: cannot read existing '{wirePath}' ({error}); transferring it whole");
        return basis;
    }

    private void TrySetTimes(string target, FileEntry entry)
    {
        if (options.PreserveTimes)
            Core.FileList.TimestampSupport.TrySetModTimeUtc(target, entry.ModTimeUnix);
    }

    private void TrySetPermissions(string target, FileEntry entry)
    {
        if (options.PreservePerms)
            Core.FileList.PermissionSupport.TryApplyReadOnly(target, entry.Mode);
    }

    private static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private void Report(string message)
    {
        if (options.Verbose > 0 || options.Stats)
            Console.WriteLine(message);
    }

    /// <summary>Joins a transfer-root path with a peer/sender wire path (forward slashes),
    /// refusing any ".." traversal the way a receiver must.</summary>
    internal static string ResolveLocal(string root, string wirePath)
    {
        string rel = wirePath.Replace('/', Path.DirectorySeparatorChar);
        if (rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Contains(".."))
            throw new InvalidOperationException($"unsafe path: {wirePath}");
        // \\?\-prefixed here (the central wire-path-to-local-path resolver) so every downstream
        // File/Directory call automatically bypasses MAX_PATH -- see LongPathSupport's own doc
        // comment for why this explicit-prefix approach was chosen over an app manifest.
        return Core.FileList.LongPathSupport.Ensure(Path.Combine(root, rel));
    }

    private static string SshHostArg(RemoteSpec spec) => spec switch
    {
        RemoteSpec.Ssh ssh => string.IsNullOrEmpty(ssh.User) ? ssh.Host : $"{ssh.User}@{ssh.Host}",
        RemoteSpec.Daemon daemon => string.IsNullOrEmpty(daemon.User) ? daemon.Host : $"{daemon.User}@{daemon.Host}",
        _ => throw new InvalidOperationException("not a remote spec"),
    };
}