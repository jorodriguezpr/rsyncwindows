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

using System.Text;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.FileList;

/// <summary>
/// Ports send_file_entry() (flist.c:475) for our v1 scope: regular files, directories, and
/// symlinks only. no atimes/crtimes/nsec/hardlinks/device-nodes/whole-file-checksum-in-list.
/// Always targets the varint-flist-flags wire path (CF_VARINT_FLIST_FLAGS), since that's what
/// <see cref="ProtocolNegotiator"/> always negotiates.
///
/// uid/gid are sent as plain numeric varints (no XMIT_USER_NAME_FOLLOWS -- a Unix
/// name-mapping concept Windows has no equivalent for, see the plan's risk #2) and ONLY when
/// the corresponding preserve flag is on -- exactly like real rsync's own send_file_entry:
/// `if (preserve_uid && !(xflags & XMIT_SAME_UID))` (flist.c:1000) and its gid twin
/// (flist.c:1011). The preserve flags come from the INVOKING SESSION's -o/-g options
/// (preserve_uid/preserve_gid, options.c) -- the peer's argv -- NOT from anything in the
/// entry itself. An earlier version wrote them unconditionally; a real peer running without
/// -o/-g left both varints unread in the stream (its recv_file_entry only consumes them
/// under the same preserve flags), desyncing everything after the flist -- the Phase 5
/// daemon-PULL "phase 2" desync and the Phase 6 -z push desync both surfaced from exactly
/// this stray-byte leak (see FileListDecoder's mirror comment).
///
/// STATEFUL, like the C implementation's own `static` locals in send_file_entry -- one
/// instance must be dedicated to a single direction of a single file-list transmission.
/// </summary>
public sealed class FileListEncoder
{
    private byte[] _lastNameBytes = [];
    private int? _mode;
    private int? _uid;
    private int? _gid;
    private long? _modTime;

    /// <param name="preserveUid">The -o/--owner state of the session this list belongs to
    /// (matches real rsync's global preserve_uid).</param>
    /// <param name="preserveGid">The -g/--group state (preserve_gid).</param>
    public void Write(Stream s, FileEntry entry, bool preserveUid = false, bool preserveGid = false)
    {
        var xflags = XmitFlags.None;

        if (_mode.HasValue && _mode.Value == entry.Mode)
            xflags |= XmitFlags.SameMode;
        else
            _mode = entry.Mode;

        if (_uid.HasValue && _uid.Value == entry.Uid)
            xflags |= XmitFlags.SameUid;
        else
            _uid = entry.Uid;

        if (_gid.HasValue && _gid.Value == entry.Gid)
            xflags |= XmitFlags.SameGid;
        else
            _gid = entry.Gid;

        if (_modTime.HasValue && _modTime.Value == entry.ModTimeUnix)
            xflags |= XmitFlags.SameTime;
        else
            _modTime = entry.ModTimeUnix;

        // Name prefix-compression against the previous entry, operating on raw UTF-8 bytes
        // (matching the C source, which just compares raw path bytes) -- mixing a char-based
        // comparison with byte-based suffix encoding would let l1 land mid-codepoint.
        byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Path);
        int maxCommon = Math.Min(Math.Min(_lastNameBytes.Length, nameBytes.Length), 255);
        int l1 = 0;
        while (l1 < maxCommon && _lastNameBytes[l1] == nameBytes[l1])
            l1++;
        ReadOnlySpan<byte> suffix = nameBytes.AsSpan(l1);
        if (l1 > 0)
            xflags |= XmitFlags.SameName;
        if (suffix.Length > 255)
            xflags |= XmitFlags.LongName;

        // A flags value of 0 would signal end-of-list, so force XMIT_EXTENDED_FLAGS on
        // instead when every bit would otherwise be clear (flist.c:644-645).
        WireCodec.WriteVarInt(s, xflags != XmitFlags.None ? (int)xflags : (int)XmitFlags.ExtendedFlags);

        if (xflags.HasFlag(XmitFlags.SameName))
            s.WriteByte((byte)l1);
        if (xflags.HasFlag(XmitFlags.LongName))
            WireCodec.WriteVarInt(s, suffix.Length);
        else
            s.WriteByte((byte)suffix.Length);
        s.Write(suffix);

        WireCodec.WriteVarLong(s, entry.Length, 3);
        if (!xflags.HasFlag(XmitFlags.SameTime))
            WireCodec.WriteVarLong(s, entry.ModTimeUnix, 4);
        if (!xflags.HasFlag(XmitFlags.SameMode))
            WireCodec.WriteInt32(s, UnixMode.ToWireMode(entry.Mode));
        // Same-uid/Same-flags are only meaningful when the field is actually being
        // transmitted; clear the bit (so it doesn't leak into the flags varint) when the
        // preserve flag is off -- flist.c:627-643 sets xflags |= XMIT_SAME_* only when the
        // static previous value matches, but a real peer with preserve off never writes or
        // expects these fields at all (recv_file_entry's gating is on preserve_uid/gid).
        if (xflags.HasFlag(XmitFlags.SameUid) && !preserveUid)
            xflags &= ~XmitFlags.SameUid;
        if (xflags.HasFlag(XmitFlags.SameGid) && !preserveGid)
            xflags &= ~XmitFlags.SameGid;
        if (preserveUid && !xflags.HasFlag(XmitFlags.SameUid))
            WireCodec.WriteVarInt(s, entry.Uid);
        if (preserveGid && !xflags.HasFlag(XmitFlags.SameGid))
            WireCodec.WriteVarInt(s, entry.Gid);

        if (entry.FileType == RsyncFileType.Symlink)
        {
            byte[] target = Encoding.UTF8.GetBytes(entry.SymlinkTarget ?? "");
            WireCodec.WriteVarInt(s, target.Length);
            s.Write(target);
        }

        _lastNameBytes = nameBytes;
    }

    /// <summary>Ports write_end_of_flist()'s varint-mode branch (flist.c:2386-2388): a zero
    /// flags varint, unconditionally followed by an io-error varint (always 0 -- v1 has no
    /// io_error tracking), matching real rsync's own unconditional write_varint(f, 0) there
    /// regardless of whether an error is actually being reported. recv_file_list()'s mirror
    /// (flist.c:2946-2951) reads both unconditionally too whenever xfer_flags_as_varint is
    /// negotiated -- which it always is here, since <see cref="Wire.ProtocolNegotiator"/> always
    /// advertises CF_VARINT_FLIST_FLAGS. An earlier version of this method sent only the first
    /// varint; that was traced (via a live byte capture against the real rsync 3.5.0 binary
    /// during Phase 4b interop) to leave a stray trailing 0x00 on the wire that got misread as
    /// an early NDX_DONE by the next reader -- restored to the real two-varint form here.
    /// </summary>
    public void WriteEndOfList(Stream s)
    {
        WireCodec.WriteVarInt(s, 0);
        WireCodec.WriteVarInt(s, 0);
    }
}
