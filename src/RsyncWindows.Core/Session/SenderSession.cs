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
/// Drives the sender role's per-file transfer negotiation loop -- ports the read_ndx_and_attrs
/// / write_ndx_and_attrs dance from send_files() (sender.c:513-646) and rsync.c's
/// read_ndx_and_attrs (rsync.c:323), plus the extra termination round-trip from
/// read_final_goodbye() (main.c:908) that a real sender performs after send_files() returns.
/// The remote generator tells us, one file at a time, which index needs a transfer (via an
/// ndx + a shortint iflags value); for each one we read its checksum table, echo the
/// ndx/iflags/sum-head back, then hand off to <see cref="DeltaSender"/> for the actual delta.
///
/// Two full "phases" (protocol &gt;= 29 always uses max_phase=2, for the redo-on-checksum-
/// mismatch retry) are acknowledged via write_ndx(NDX_DONE) even though this v1 has no
/// checksum-mismatch redo logic of its own to contribute -- we simply have nothing to
/// re-send in phase 2. Getting the read_final_goodbye tail wrong was confirmed against the
/// real rsync-3.5.0 binary during Phase 4 interop testing: omitting it left the real
/// receiver mid-protocol expecting one more NDX_DONE handshake, which one real run surfaced
/// as "got transfer request in phase 2" once a stale connection's leftover bytes were
/// misread as a fresh request on a subsequent attempt.
/// </summary>
public static class SenderSession
{
    private const int MaxPhase = 2;

    public sealed record FileToSend(FileEntry Entry, byte[] Data);

    /// <param name="writeDaemonStats">Set when this sender role is being played by a daemon
    /// (<c>am_daemon &amp;&amp; am_server &amp;&amp; am_sender</c>) -- do_server_sender() (main.c:993-997)
    /// calls `handle_stats(f_out)` with a real fd in that combination specifically, which writes
    /// 5 `write_varlong30` values (total_read, total_written, total_size, then -- protocol &gt;= 29
    /// -- flist_buildtime and flist_xfertime) before the final-goodbye handshake. Every other
    /// combination this v1 exercises (an SSH-piped client_run() sender, i.e. Phase 4a/4b) calls
    /// `handle_stats(-1)` instead, which writes nothing -- so this must stay off for those paths.
    /// Found via a real interop failure once Phase 5 (daemon mode) started exercising this
    /// specific role combination for the first time: the peer's generator/receiver blocked
    /// reading these 5 values while we jumped straight to the goodbye handshake, desyncing the
    /// stream by exactly 5 varlong fields and surfacing as an abrupt connection reset. The
    /// values themselves are informational only (rsync's own `--stats`/`-v` summary output) and
    /// are not re-validated by the receiver, so exact byte-for-byte accuracy isn't required for
    /// correctness -- only their presence, count, and order on the wire.</param>
    /// <param name="compress">When true, frames each file's token stream through a fresh
    /// per-file <see cref="Delta.CompressedTokenWriter"/> (`-z` wire format) instead of the
    /// plain framing -- caller must have already negotiated compression with the peer
    /// (<see cref="Wire.ProtocolNegotiator.Negotiate"/>'s `compress` parameter) so both sides
    /// agree before any token bytes are framed this way.</param>
    /// <param name="onFileStart">Invoked right as each requested file's transfer begins (mirrors
    /// real rsync's own `-v` timing: the name prints when sending starts, not when it finishes)
    /// -- optional, since a caller with no interactive console (e.g. a daemon connection with
    /// its own separate log sink) has nothing to do with it.</param>
    public static void RunSenderLoop(Stream input, Stream output, IReadOnlyList<FileToSend> files, int checksumSeed, bool writeDaemonStats = false, bool compress = false, Action<FileEntry>? onFileStart = null)
    {
        var ndxIn = new NdxDecoder();
        var ndxOut = new NdxEncoder();
        int phase = 0;

        while (true)
        {
            int ndx = ndxIn.Read(input);
            if (ndx == Ndx.Done)
            {
                phase++;
                if (phase > MaxPhase)
                    break;
                ndxOut.Write(output, Ndx.Done);
                continue;
            }

            var iflags = (ItemFlags)WireCodec.ReadInt16(input);

            if (iflags.HasFlag(ItemFlags.BasisTypeFollows))
                WireCodec.ReadByte(input); // fnamecmp_type -- no alt-dest-basis support in v1
            if (iflags.HasFlag(ItemFlags.XNameFollows))
                WireCodec.ReadVString(input);

            if (!iflags.HasFlag(ItemFlags.Transfer))
                continue; // nothing to do for this file (not expected to occur for a fresh push)

            if (ndx < 0 || ndx >= files.Count)
                throw new InvalidOperationException($"generator requested unknown file index {ndx}");

            var checksums = ChecksumTable.ReadFromWire(input);
            onFileStart?.Invoke(files[ndx].Entry);

            ndxOut.Write(output, ndx);
            WireCodec.WriteInt16(output, (ushort)iflags);
            checksums.Head.Write(output); // re-emit just the header, not the blocks (sender.c:767)
            if (compress)
            {
                using var compressor = new Delta.CompressedTokenWriter();
                DeltaSender.MatchAndSend(files[ndx].Data, checksums, checksumSeed, output, compressor);
            }
            else
            {
                DeltaSender.MatchAndSend(files[ndx].Data, checksums, checksumSeed, output);
            }
        }

        // send_files()'s own tail (sender.c:817): after the loop above breaks (which happens
        // without writing anything, on the 3rd NDX_DONE read once phase > MaxPhase), real
        // send_files() unconditionally writes one more NDX_DONE before returning -- distinct
        // from the per-phase acks the loop already wrote, and distinct from read_final_goodbye's
        // own separate handshake below. Missing this specifically breaks the daemon-as-sender
        // role (do_server_sender() calls handle_stats(f_out) -- see writeDaemonStats below --
        // immediately after send_files() returns): the peer's recv_files() is still waiting for
        // this exact NDX_DONE and misreads the stats varlongs that follow as protocol data
        // instead, corrupting everything downstream (observed as a wildly wrong "total size"
        // and no file ever landing). Kept unconditional here (matching the real C source) rather
        // than gating it on writeDaemonStats, even though Phase 4a/4b's SSH-piped scenario
        // apparently tolerated its absence.
        ndxOut.Write(output, Ndx.Done);

        if (writeDaemonStats)
        {
            long totalSize = 0;
            foreach (var f in files)
                totalSize += f.Entry.Length;
            WireCodec.WriteVarLong(output, 0, 3);         // total_read (approximate -- informational only)
            WireCodec.WriteVarLong(output, totalSize, 3); // total_written (approximate -- informational only)
            WireCodec.WriteVarLong(output, totalSize, 3); // total_size
            WireCodec.WriteVarLong(output, 1, 3);          // flist_buildtime (protocol >= 29)
            WireCodec.WriteVarLong(output, 1, 3);          // flist_xfertime (protocol >= 29)
            output.Flush();
        }

        // read_final_goodbye() (main.c:908) -- for protocol >= 31 with am_sender this is a
        // THREE-step handshake, not one read: read one NDX_DONE, WRITE one NDX_DONE back
        // (the `if (protocol_version >= 31 && i == NDX_DONE) { if (am_sender)
        // write_ndx(f_out, NDX_DONE); ... }` branch, main.c:924-928), then read one more
        // NDX_DONE. The generator/receiver on the other side is exactly the do_recv()
        // topology: the receiver child writes its post-loop DONE + MSG_STATS, then runs its
        // own read_final_goodbye() which (as am_receiver, protocol >= 31) reads our DONE and
        // echoes one back before exiting (main.c:1149-1157 + read_final_goodbye's
        // am_receiver branch, main.c:928-932); the generator parent also writes one final
        // DONE after generate_files returns (do_recv's tail, main.c:1163). An earlier
        // version here performed only the final read: our socket then closed while the real
        // receiver was still inside its own read_final_goodbye, surfacing on the client as
        // "connection unexpectedly closed (256 bytes received so far)" right after a fully
        // correct file transfer (Phase 5 interop, fixed by writing the middle DONE).
        int finalNdx = ndxIn.Read(input);
        if (finalNdx != Ndx.Done)
            throw new InvalidOperationException($"Invalid packet at end of run ({finalNdx})");
        ndxOut.Write(output, Ndx.Done);
        output.Flush();
        int finalNdx2 = ndxIn.Read(input);
        if (finalNdx2 != Ndx.Done)
            throw new InvalidOperationException($"Invalid packet at end of run ({finalNdx2})");
    }
}
