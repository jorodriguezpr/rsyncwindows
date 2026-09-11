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

using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.FileList;

/// <summary>
/// Ports send_id_lists()/recv_id_list() (uidlist.c) -- a SEPARATE block a sender writes
/// immediately after the file list's own end-of-list terminator (and before the transfer
/// request loop begins), present whenever <c>numeric_ids &lt;= 0</c> (the default -- i.e. NOT
/// passing <c>--numeric-ids</c>, which this project's CLI doesn't implement/emit) AND
/// <c>!inc_recurse</c> (this project never advertises incremental-recursion support, so a real
/// peer always falls back to this classic path against us). Real rsync sends this to let the
/// receiver map numeric uid/gid values back to names on ITS OWN system; since Windows has no
/// equivalent uid/gid-to-name mapping to offer, this port always sends an EMPTY name per id
/// (`send_one_name`'s own `len=0` case, which real rsync's `recv_user_name`/`recv_group_name`
/// both handle as "no name, numeric only" -- see uidlist.c) -- the wire SHAPE is what matters for
/// staying in sync with the peer, not the name content, which this v1 never uses either way (no
/// uid/gid-by-name mapping on Windows).
///
/// Gated ONLY by preserveUid/preserveGid (the `-o`/`-g` session flags) -- exactly the condition
/// real rsync's own `preserve_uid || preserve_acls` (and gid's twin) checks, minus ACLs (not
/// implemented here).
///
/// Missing this block was a real, previously-undiscovered bug: every prior interop test either
/// omitted `-o`/`-g` entirely or exercised only this project's own client/daemon talking to
/// itself (which never expected this block either, so the omission was self-consistent and
/// invisible) -- it first surfaced against a genuine rsync client's `-og`/`-a` push to the LIVE
/// installed daemon, desyncing the very first post-flist read ("sender echoed ndx N, expected 0")
/// because the id-list bytes a real sender actually put on the wire were never consumed.
///
/// **`id0Names` (CF_ID0_NAMES / `xmit_id0_names`, uidlist.c:389-406)**: real rsync's own
/// `send_one_list` doesn't terminate the list with a bare `varint(0)` when this compat flag is
/// negotiated -- it replaces the terminator with a full `send_one_name(f, 0, name)` call (id 0's
/// OWN entry: the same `varint(0)` doubling as both the id and the loop terminator, immediately
/// followed by a length byte, matching every other entry's shape). `recv_id_list` mirrors this:
/// after its `while ((id = read_varint30(f)) != 0)` loop exits on the id-0 terminator, it
/// unconditionally reads ONE MORE length byte for id 0 whenever this flag is set. This project's
/// own SSH-invoked peer negotiation (<see cref="Options.ServerInvocationBuilder"/>'s
/// `e32.0fxCIvu` client_info suffix) advertises the `u` letter that turns this flag on for a
/// REAL rsync peer, but this codec originally never wrote/read the extra byte -- a genuine rsync
/// receiver's `recv_user_name`/`recv_group_name` call for id 0 then blocked forever waiting for a
/// length byte that would never arrive (and symmetrically for a genuine rsync sender we'd be
/// receiving from), a silent mutual deadlock right after the flist ("received N names" is the
/// last thing either side logs) -- confirmed live via a real rsync 3.5.0 `--server` process over
/// a local TCP bridge (bypassing an unrelated `wsl.exe` stdio-corruption issue that blocks
/// spawning it as a direct child process for this kind of repro), and via `compat.c`'s own
/// `CF_ID0_NAMES`/`xmit_id0_names` source. This project's OWN `--server`-responder role
/// (<see cref="Wire.ProtocolNegotiator.Negotiate"/>'s `isServer` branch) never advertises this
/// flag itself, so this only ever matters talking to a genuine external rsync peer, and only in
/// the direction THIS project initiated (SSH push/pull) -- daemon-mode transfers and anything
/// where this project is the `--server` responder are unaffected, since the flag's value there
/// is always whatever THIS project computed (never includes it), not what the client_info string
/// claimed.
/// </summary>
public static class IdListCodec
{
    /// <summary>Writer side (this process is the flist SENDER) -- call right after
    /// <see cref="FileListEncoder.WriteEndOfList"/>, before flushing. <paramref name="id0Names"/>
    /// must be the negotiated session's <see cref="Wire.CompatFlags.Id0Names"/> state -- see the
    /// class doc comment.</summary>
    public static void WriteIdLists(Stream s, IEnumerable<FileEntry> entries, bool preserveUid, bool preserveGid, bool id0Names = false)
    {
        if (preserveUid)
            WriteOneList(s, entries.Select(e => e.Uid), id0Names);
        if (preserveGid)
            WriteOneList(s, entries.Select(e => e.Gid), id0Names);
    }

    private static void WriteOneList(Stream s, IEnumerable<int> ids, bool id0Names)
    {
        // id 0 is never listed (rsync's own uidlist.c: "Never do any mapping for uid=0 or
        // gid=0 as these are special") -- it doubles as the list terminator instead.
        foreach (int id in ids.Where(id => id != 0).Distinct())
        {
            WireCodec.WriteVarInt(s, id);
            s.WriteByte(0); // empty name -- see class doc comment
        }
        WireCodec.WriteVarInt(s, 0);
        if (id0Names)
            s.WriteByte(0); // id 0's own (empty) name -- see class doc comment's CF_ID0_NAMES section
    }

    /// <summary>Reader side (this process is the flist RECEIVER) -- call right after the flist
    /// decode loop finishes, before issuing any transfer requests. <paramref name="id0Names"/>
    /// must be the negotiated session's <see cref="Wire.CompatFlags.Id0Names"/> state -- see the
    /// class doc comment.</summary>
    public static void ReadIdLists(Stream s, bool preserveUid, bool preserveGid, bool id0Names = false)
    {
        if (preserveUid)
            ReadOneList(s, id0Names);
        if (preserveGid)
            ReadOneList(s, id0Names);
    }

    private static void ReadOneList(Stream s, bool id0Names)
    {
        while (true)
        {
            int id = WireCodec.ReadVarInt(s);
            if (id == 0)
            {
                if (id0Names)
                    DiscardName(s);
                return;
            }
            DiscardName(s);
        }
    }

    private static void DiscardName(Stream s)
    {
        int len = WireCodec.ReadByte(s);
        if (len > 0)
        {
            var discard = new byte[len];
            s.ReadExactly(discard);
        }
    }
}
