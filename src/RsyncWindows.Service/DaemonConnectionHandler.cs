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
using System.Text;
using RsyncWindows.Core.Daemon;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Service;

/// <summary>
/// Composes one accepted daemon connection end to end: <see cref="DaemonHandshake"/> ->
/// <see cref="ProtocolNegotiator"/> -> a sender or receiver session, plus the real disk I/O
/// (directory walk, file reads/writes) that Core deliberately keeps out of its own session
/// classes (<see cref="SenderSession"/>/<see cref="ReceiverSession"/> both take/return in-memory
/// byte arrays via caller-supplied callbacks, matching how the interop tests already do this at
/// the call site -- see PushSingleFileToRealRsyncTests' PushOneFile helper). This is the
/// composition root for that wiring, matching the plan's Phase 5 scope: a Windows Service
/// acting as a real rsync daemon against the module tree in rsyncd.conf.
///
/// v1 scope: the whole module tree is always the transfer root (no subpath-under-module
/// requests, e.g. `module/subdir`) -- matching most simple daemon deployments (`rsync -a
/// host::module local/`), and consistent with how RsyncdModule itself has no notion of
/// per-request path drilling yet. A read-only violation is discovered only after protocol
/// negotiation completes (see DaemonHandshake.RunServerSide's doc comment on why it can't be
/// caught earlier without breaking wire compatibility) and reported as a normal MSG_ERROR frame.
/// </summary>
public sealed class DaemonConnectionHandler(RsyncdConfig config, Action<string> log)
{
    public void Handle(DuplexStreamPair raw, IPAddress clientAddress)
    {
        DaemonHandshake.ServerRequest? request;
        try
        {
            request = DaemonHandshake.RunServerSide(raw, config, clientAddress, log);
        }
        catch (Exception ex)
        {
            log($"handshake with {clientAddress} failed: {ex.Message}");
            return;
        }
        if (request == null)
            return;

        var session = ProtocolNegotiator.Negotiate(
            raw,
            isServer: true,
            onMessage: (code, payload) => log($"[peer {code}] {Encoding.UTF8.GetString(payload).TrimEnd()}"),
            preNegotiatedVersion: request.RemoteProtocolVersion,
            compress: request.Compress);

        if (!request.AmSender && request.Module.ReadOnly)
        {
            log($"refused write to read-only module '{request.Module.Name}' from {clientAddress}");
            session.Output.WriteMessage(MsgCode.Error, Encoding.UTF8.GetBytes($"ERROR: module {request.Module.Name} is read only\n"));
            session.Output.Flush();
            return;
        }

        if (request.AmSender)
            RunAsSender(session, request.Module, clientAddress, request);
        else
            RunAsReceiver(session, request.Module, clientAddress, request);
    }

    private void RunAsSender(NegotiatedSession session, RsyncdModule module, IPAddress clientAddress, DaemonHandshake.ServerRequest request)
    {
        // recv_filter_list() (exclude.c:1971): start_server()'s am_sender branch (main.c)
        // consumes the client's filter-rule list before do_server_sender() ever runs. The
        // client always sends this -- send_filter_list() (exclude.c:1944) unconditionally
        // write_int(f_out, 0)'s a terminator even with zero rules -- so skipping this read
        // leaves a 4-byte zero int sitting in the stream. NdxDecoder.Read() only consumes one
        // byte per NDX_DONE sentinel, so those 4 zero bytes get misread as up to four separate
        // NDX_DONE signals once SenderSession starts reading, corrupting the entire exchange
        // before the real transfer request is ever seen. Confirmed via a live byte trace
        // against the real rsync 3.5.0 binary during Phase 5 interop: the generator's genuine
        // ndx=0 request was queued right behind this terminator and never got read at all.
        RecvFilterList(session.Input);

        // Read every regular file BEFORE the flist goes out: the client requests files by flist
        // index, so one we can't read (locked, access denied) is left out of the list up front
        // instead of throwing mid-transfer and dropping the whole connection.
        var entries = new List<FileEntry>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in FileListBuilder.Walk(module.Path))
        {
            if (entry.FileType == RsyncFileType.Regular)
            {
                if (!FileReadSupport.TryReadFile(ResolvePath(module.Path, entry.Path), out byte[] content, out string? readError))
                {
                    log($"skipped '{entry.Path}' for {clientAddress} (cannot read source): {readError}");
                    continue;
                }
                contents[entry.Path] = content;
            }
            entries.Add(entry);
        }

        // The file-list field set depends on the SESSION's preserve options, which come from
        // the client's own --server argv (both sides parse the same bundled flags; see
        // DaemonHandshake.HasShortFlag). -o maps to preserve_uid, -g to preserve_gid
        // (options.c's case 'o'/'g'). Getting these wrong desyncs the flist terminator:
        // fields written but not expected (or vice versa) leave stray bytes that a real
        // peer's subsequent NDX bookkeeping misreads.
        bool preserveUid = request.ServerArgs.Any(a => HasShortFlag(a, 'o'));
        bool preserveGid = request.ServerArgs.Any(a => HasShortFlag(a, 'g'));

        var encoder = new FileListEncoder();
        foreach (var entry in entries)
            encoder.Write(session.Output, entry, preserveUid, preserveGid);
        encoder.WriteEndOfList(session.Output);
        IdListCodec.WriteIdLists(session.Output, entries, preserveUid, preserveGid, session.CompatFlags.HasFlag(CompatFlags.Id0Names));
        session.Output.Flush();

        var files = entries
            .Select(e => new SenderSession.FileToSend(e, e.FileType == RsyncFileType.Regular ? contents[e.Path] : []))
            .ToList();

        SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed, writeDaemonStats: true, compress: session.CompressionEnabled);
        session.Output.Flush();

        log($"sent {files.Count(f => f.Entry.FileType == RsyncFileType.Regular)} file(s) from module '{module.Name}' to {clientAddress}");
    }

    private void RunAsReceiver(NegotiatedSession session, RsyncdModule module, IPAddress clientAddress, DaemonHandshake.ServerRequest request)
    {
        Directory.CreateDirectory(module.Path);

        // Same argv-derived preserve flags as RunAsSender -- the mirror flist read must
        // agree with what the peer's sender wrote.
        bool preserveUid = request.ServerArgs.Any(a => HasShortFlag(a, 'o'));
        bool preserveGid = request.ServerArgs.Any(a => HasShortFlag(a, 'g'));
        bool preserveTimes = request.ServerArgs.Any(a => HasShortFlag(a, 't'));
        bool preservePerms = request.ServerArgs.Any(a => HasShortFlag(a, 'p'));

        var received = ReceiverSession.RunReceiverLoop(session.Input, session.Output, path => ReadBasis(module.Path, path, clientAddress), session.ChecksumSeed,
            compress: session.CompressionEnabled, preserveUid: preserveUid, preserveGid: preserveGid,
            id0Names: session.CompatFlags.HasFlag(CompatFlags.Id0Names),
            onNonTransferEntry: e => ApplyNonTransferEntry(module.Path, e, clientAddress));

        int skipped = 0;
        foreach (var file in received)
        {
            string full = ResolvePath(module.Path, file.Entry.Path);
            if (!FileWriteSupport.TryWriteFile(full, file.Data, out string? writeError))
            {
                log($"skipped '{file.Entry.Path}' from {clientAddress}: {writeError}");
                skipped++;
                continue;
            }
            if (preserveTimes)
                TimestampSupport.TrySetModTimeUtc(full, file.Entry.ModTimeUnix);
            if (preservePerms)
                PermissionSupport.TryApplyReadOnly(full, file.Entry.Mode);
        }

        log($"received {received.Count - skipped} file(s) ({skipped} skipped) into module '{module.Name}' from {clientAddress}");
    }

    /// <summary>Applies a Directory or Symlink flist entry directly to disk -- these never go
    /// through <see cref="SenderSession"/>/<see cref="ReceiverSession"/>'s transfer-request
    /// loop (a directory has no content of its own; a symlink's target string is already
    /// complete from the flist). Both degrade gracefully (log and skip) rather than failing the
    /// whole CONNECTION: symlinks when the process lacks SeCreateSymbolicLinkPrivilege (see
    /// <see cref="RsyncWindows.Core.FileList.SymlinkSupport"/>), directories when the peer's name
    /// is legal on ITS filesystem but not NTFS -- see
    /// <see cref="RsyncWindows.Core.FileList.FileWriteSupport"/>'s doc comment: this used to be
    /// an unhandled exception that killed the entire connection over ONE bad name (confirmed
    /// live: a real push containing a directory literally named "C:" -- legal on the Linux
    /// sender, illegal on NTFS -- aborted the whole transfer, losing every other file in it).</summary>
    private void ApplyNonTransferEntry(string moduleRoot, FileEntry entry, IPAddress clientAddress)
    {
        string full = ResolvePath(moduleRoot, entry.Path);
        if (entry.FileType == RsyncFileType.Directory)
        {
            if (!FileWriteSupport.TryCreateDirectory(full, out string? error))
                log($"skipped directory '{entry.Path}' from {clientAddress}: {error}");
        }
        else if (entry.FileType == RsyncFileType.Symlink && entry.SymlinkTarget != null)
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !FileWriteSupport.TryCreateDirectory(dir, out string? dirError))
            {
                log($"skipped symlink '{entry.Path}' from {clientAddress}: {dirError}");
                return;
            }
            if (!RsyncWindows.Core.FileList.SymlinkSupport.TryCreateSymlink(full, entry.SymlinkTarget, out string? error))
                log($"skipped symlink '{entry.Path}' from {clientAddress}: {error}");
        }
    }

    /// <summary>The existing file as the delta basis; one that can't be read (access denied,
    /// locked) becomes an empty basis -- whole-file transfer, as real rsync does -- instead of an
    /// exception that drops the client's connection.</summary>
    private byte[] ReadBasis(string moduleRoot, string relativePath, IPAddress clientAddress)
    {
        byte[] basis = FileReadSupport.ReadBasisOrEmpty(ResolvePath(moduleRoot, relativePath), out string? error);
        if (error != null)
            log($"cannot read existing '{relativePath}' for {clientAddress} ({error}); transferring it whole");
        return basis;
    }

    // \\?\-prefixed so every downstream File/Directory call bypasses MAX_PATH -- see
    // LongPathSupport's doc comment for why (an app-manifest approach broke the exe outright).
    private static string ResolvePath(string moduleRoot, string relativePath) =>
        LongPathSupport.Ensure(Path.Combine(moduleRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Ports recv_filter_list()'s actual read loop (exclude.c:1980-1985): a 4-byte
    /// length-prefixed string repeated until a zero length terminates it. This v1 has no
    /// exclude/include/filter support of its own, so the rules are simply discarded -- the
    /// point is draining exactly the bytes the client unconditionally sends, not acting on them.</summary>
    private static void RecvFilterList(Stream input)
    {
        while (true)
        {
            int len = WireCodec.ReadInt32(input);
            if (len == 0)
                return;
            var discard = new byte[len];
            input.ReadExactly(discard);
        }
    }

    /// <summary>Mirror of DaemonHandshake.HasShortFlag: scans a bundled short-option token
    /// (e.g. "-vlze32.0fxCIvu") for one flag letter, stopping at 'e' (the protocol/capability
    /// suffix starts there). Kept local so the Service project doesn't have to reach into the
    /// Daemon namespace's private helper.</summary>
    private static bool HasShortFlag(string arg, char flag)
    {
        if (arg.Length < 2 || arg[0] != '-' || arg[1] == '-')
            return false;
        for (int i = 1; i < arg.Length; i++)
        {
            if (arg[i] == 'e')
                return false;
            if (arg[i] == flag)
                return true;
        }
        return false;
    }
}
