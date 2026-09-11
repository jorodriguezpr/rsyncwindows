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

using RsyncWindows.Core.Delta;
using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.Session;

/// <summary>
/// Drives the combined generator+receiver role's side of a transfer -- the mirror image of
/// <see cref="SenderSession"/>, played when a real rsync client pushes files TO us (Phase 4b).
/// Real rsync splits this role into two forked processes (generator decides what needs
/// transferring and requests it; receiver reads the sender's replies and reconstructs files)
/// communicating over the SAME external stream in different directions; this v1 plays both
/// halves sequentially in one loop since there's no need for the concurrency real rsync's
/// fork buys it (it exists there to let file-scanning/requesting overlap with
/// receiving/writing, not because the protocol requires two separate processes).
///
/// Per-file wire shape mirrors send_files()/read_ndx_and_attrs() from the OTHER side (see
/// SenderSession's doc comment): for each file that needs a transfer, WE write ndx + iflags +
/// a real (possibly non-empty) checksum table for whatever basis file already exists locally,
/// then read back the sender's echoed ndx/iflags/sum-head-echo and its token stream, handing
/// off to <see cref="DeltaReceiver"/> to reconstruct.
///
/// Termination sequence empirically confirmed against the real rsync-3.5.0 binary (Phase 4b
/// interop testing): mirrors what SenderSession found from the other side -- we write
/// NDX_DONE three times in a row (driving the real sender's send_files() loop through its two
/// phase-transition replies and then its silent break on the third), then read the sender's
/// two replies, then write one more NDX_DONE (matching what the sender's own read_final_goodbye
/// expects as its final confirmation, with no reply required back to us).
/// </summary>
public static class ReceiverSession
{
    public sealed record ReceivedFile(FileEntry Entry, byte[] Data);

    /// <param name="input">Raw (post-negotiation, multiplexed) input from the sender.</param>
    /// <param name="output">Raw (post-negotiation, multiplexed) output to the sender.</param>
    /// <param name="getBasis">Given a file's wire path, returns its existing local content
    /// (or an empty array if it doesn't exist locally yet).</param>
    /// <param name="checksumSeed">The seed negotiated for this session.</param>
    /// <param name="compress">When true, reads each file's token stream through a fresh per-file
    /// <see cref="Delta.CompressedTokenReader"/> (`-z` wire format) -- caller must have already
    /// negotiated compression with the peer (<see cref="Wire.ProtocolNegotiator.Negotiate"/>'s
    /// `compress` parameter) so both sides agree before any token bytes are framed this way.</param>
    /// <param name="preserveUid">The -o/--owner state of this session's argv (preserve_uid) --
    /// decides whether the peer's file list carries uid varints (flist.c:1000 gating).</param>
    /// <param name="preserveGid">The -g/--group state (preserve_gid) -- same gating for gid
    /// (flist.c:1011).</param>
    /// <param name="id0Names">The negotiated session's <see cref="Wire.CompatFlags.Id0Names"/>
    /// state -- see <see cref="FileList.IdListCodec"/>'s doc comment for why a genuine rsync
    /// sender's id-list block needs this to stay in sync.</param>
    /// <param name="onNonTransferEntry">Invoked once per Directory/Symlink entry as the flist is
    /// walked (these never go through a transfer request -- a symlink's target string is
    /// already complete from the flist alone, and a directory has no content of its own).
    /// Optional and additive: existing callers that only care about regular-file transfers can
    /// leave this null. A caller that wants directories (including ones with no files inside,
    /// which would otherwise never get created at all) or symlinks actually created on disk
    /// passes a callback here -- see <see cref="RsyncWindows.Core.FileList.SymlinkSupport"/> for
    /// the symlink half.</param>
    /// <param name="onFileStart">Invoked right as each regular file's transfer request is issued
    /// to the peer (mirrors real rsync's own `-v` timing) -- optional, since a caller with no
    /// interactive console (a daemon connection's own separate log sink) has nothing to do with
    /// it.</param>
    /// <param name="sendFilterList">Set when this side INITIATED the connection as a puller
    /// (<c>ClientRunner.PullInto</c>, over either SSH or daemon transport) -- true there, false
    /// for every other caller. send_filter_list() (exclude.c:1944) is sent by the generator role
    /// specifically when its peer is a real `--server --sender` responder (`start_server()`'s
    /// am_sender branch / `do_server_sender()`, main.c), which unconditionally reads one before
    /// walking its own file list (see <c>DaemonConnectionHandler.RunAsSender</c>'s matching
    /// `RecvFilterList`) -- REGARDLESS of SSH vs daemon transport, since both route through the
    /// same `--server --sender` responder code. It must NOT be sent when this side is itself the
    /// `--server`-invoked RECEIVER responding to a real (or our own) `client_run()`-style pushing
    /// sender (<c>DaemonConnectionHandler.RunAsReceiver</c>, <c>Cli.ServerRole.Run</c>'s receiver
    /// branch) -- that peer's sender role never reads one, so sending it there desyncs the very
    /// next read instead (confirmed directly: <see cref="SenderSession.RunSenderLoop"/>'s first
    /// `NdxDecoder.Read` misinterpreted the stray 4 bytes as a garbage ndx value). Omitting this
    /// entirely (the original bug) instead hangs the SENDER peer forever in the pull case.</param>
    public static IReadOnlyList<ReceivedFile> RunReceiverLoop(
        Stream input, Stream output, Func<string, byte[]> getBasis, int checksumSeed, bool compress = false,
        bool preserveUid = false, bool preserveGid = false, bool id0Names = false, Action<FileEntry>? onNonTransferEntry = null,
        Action<FileEntry>? onFileStart = null, bool sendFilterList = false)
    {
        if (sendFilterList)
        {
            WireCodec.WriteInt32(output, 0);
            output.Flush();
        }

        var flistDecoder = new FileListDecoder();
        var decodedEntries = new List<FileEntry>();
        FileEntry? entry;
        while ((entry = flistDecoder.Read(input, preserveUid, preserveGid)) != null)
            decodedEntries.Add(entry);

        // The sender's own ndx numbering (what we must request BY NUMBER below) is based on
        // its GLOBAL sort of the flattened flist, NOT the order entries actually arrive on the
        // wire -- see RsyncFileOrder's doc comment ("Bug 8"). Re-sort here so our own ndx
        // assignment (loop index into `entries`) matches what the peer expects.
        var entries = RsyncFileOrder.Sort(decodedEntries);

        // send_id_lists()/recv_id_list() (uidlist.c) -- a real sender follows its flist with a
        // uid-name-list and/or gid-name-list block whenever preserveUid/preserveGid is set (see
        // IdListCodec's doc comment). Must be read here, before any transfer request, or every
        // subsequent read desyncs against the leftover bytes.
        IdListCodec.ReadIdLists(input, preserveUid, preserveGid, id0Names);

        // Checked here (pure string comparison, no disk access) rather than left to each
        // caller: every receive path writes to a case-insensitive-but-preserving Windows
        // filesystem, so this risk is universal, not caller-specific. Throws before any file is
        // written -- see CaseCollisionDetector's doc comment.
        CaseCollisionDetector.ThrowIfCollisions(entries);

        var ndxOut = new NdxEncoder();
        var ndxIn = new NdxDecoder();
        var results = new List<ReceivedFile>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].FileType != RsyncFileType.Regular)
            {
                onNonTransferEntry?.Invoke(entries[i]);
                continue; // v1: only regular-file transfers are requested (see class doc comment)
            }

            onFileStart?.Invoke(entries[i]);
            byte[] basis = getBasis(entries[i].Path);
            bool isNew = basis.Length == 0;
            var iflags = ItemFlags.Transfer | (isNew ? ItemFlags.IsNew : ItemFlags.None);

            ndxOut.Write(output, i);
            WireCodec.WriteInt16(output, (ushort)iflags);
            var checksums = ChecksumTable.ComputeAndWrite(basis, checksumSeed, output);
            output.Flush();

            int echoedNdx = ndxIn.Read(input);
            if (echoedNdx != i)
                throw new InvalidOperationException($"sender echoed ndx {echoedNdx}, expected {i}");
            WireCodec.ReadInt16(input); // echoed iflags -- not re-validated in v1
            var echoedHead = SumHead.Read(input);

            DeltaResult result;
            if (compress)
            {
                using var compressor = new Delta.CompressedTokenReader();
                result = DeltaReceiver.Reconstruct(basis, echoedHead, input, compressor);
            }
            else
            {
                result = DeltaReceiver.Reconstruct(basis, echoedHead, input);
            }
            if (!result.DigestMatches)
                throw new InvalidOperationException($"whole-file digest mismatch reconstructing '{entries[i].Path}'");

            results.Add(new ReceivedFile(entries[i], result.Reconstructed));
        }

        // Drive the sender's send_files() through its two phase transitions, then its silent
        // break on the third -- see class doc comment.
        ndxOut.Write(output, Ndx.Done);
        ndxOut.Write(output, Ndx.Done);
        ndxOut.Write(output, Ndx.Done);
        output.Flush();

        int reply1 = ndxIn.Read(input);
        int reply2 = ndxIn.Read(input);
        if (reply1 != Ndx.Done || reply2 != Ndx.Done)
            throw new InvalidOperationException($"expected two NDX_DONE replies, got {reply1} and {reply2}");

        // The sender's read_final_goodbye() expects one more NDX_DONE, with no reply back --
        // this is SenderSession's `finalNdx` read.
        ndxOut.Write(output, Ndx.Done);
        output.Flush();

        // SenderSession's own goodbye handshake (protocol >= 31, am_sender) doesn't stop there:
        // after its finalNdx read succeeds, it ALSO writes one more NDX_DONE back and then reads
        // a THIRD reply (see that class's doc comment on read_final_goodbye's three-step shape,
        // main.c:924-932) -- real rsync's receiver-role read_final_goodbye() (am_receiver branch,
        // main.c:928-932) is what supplies that pair: read the sender's echoed DONE, write one
        // final DONE back. Without this, SenderSession's last read blocks forever waiting for a
        // reply this method never sent -- only surfaces when OUR OWN sender and receiver talk to
        // each other directly (every real-peer interop test tolerates the gap, since a real peer
        // on either side already implements its own side of this correctly), which is exactly
        // how it was found: ReceiverSessionNonTransferEntryTests is the first test to run our
        // own SenderSession and ReceiverSession against each other rather than against a real
        // rsync binary.
        int reply3 = ndxIn.Read(input);
        if (reply3 != Ndx.Done)
            throw new InvalidOperationException($"expected sender's echoed NDX_DONE, got {reply3}");
        ndxOut.Write(output, Ndx.Done);
        output.Flush();

        return results;
    }
}
