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

/// <summary>Counterpart to <see cref="FileListEncoder"/>, porting recv_file_entry()
/// (flist.c:777) for the same v1 scope. Also stateful, for the same reason.
///
/// uid/gid are read ONLY when the corresponding preserve flag is on (the caller derives
/// those from the session's -o/-g options) -- recv_file_entry's own gating is
/// `if (preserve_uid && !(xflags & XMIT_SAME_UID))` (flist.c:1000) / its gid twin
/// (flist.c:1011). An earlier version read them unconditionally, so when a real peer sent
/// a list WITHOUT -o/-g the decoder consumed two varints the peer never sent, desyncing
/// the flist end -- the mirror image of the encoder-side bug (see FileListEncoder's doc
/// comment for the two real-interop failures this produced).</summary>
public sealed class FileListDecoder
{
    private byte[] _lastNameBytes = [];
    private int _mode;
    private int _uid;
    private int _gid;
    private long _modTime;

    /// <param name="preserveUid">The -o/--owner state of the session this list belongs to
    /// (matches real rsync's global preserve_uid).</param>
    /// <param name="preserveGid">The -g/--group state (preserve_gid).</param>
    public FileEntry? Read(Stream s, bool preserveUid = false, bool preserveGid = false)
    {
        int rawFlags = WireCodec.ReadVarInt(s);
        if (rawFlags == 0)
        {
            // write_end_of_flist() (flist.c:2384-2394) unconditionally follows the zero flags
            // varint with a second io_error varint whenever xfer_flags_as_varint is negotiated
            // (flist.c:2386-2388) -- which is always the case here, since ProtocolNegotiator
            // always advertises CF_VARINT_FLIST_FLAGS. recv_file_list()'s mirror (flist.c:2946-
            // 2951) reads it unconditionally too. Confirmed via a live byte trace against the
            // real rsync 3.5.0 binary during Phase 4b interop: the real client's terminator left
            // one trailing 0x00 byte that a single-varint read didn't consume, which then got
            // misread as the sender's NDX_DONE reply to our very first transfer request.
            WireCodec.ReadVarInt(s);
            return null;
        }
        var xflags = (XmitFlags)rawFlags;

        int l1 = xflags.HasFlag(XmitFlags.SameName) ? WireCodec.ReadByte(s) : 0;
        int l2 = xflags.HasFlag(XmitFlags.LongName) ? WireCodec.ReadVarInt(s) : WireCodec.ReadByte(s);

        var nameBytes = new byte[l1 + l2];
        _lastNameBytes.AsSpan(0, l1).CopyTo(nameBytes);
        s.ReadExactly(nameBytes.AsSpan(l1, l2));
        _lastNameBytes = nameBytes;
        string path = Encoding.UTF8.GetString(nameBytes);

        long length = WireCodec.ReadVarLong(s, 3);
        if (!xflags.HasFlag(XmitFlags.SameTime))
            _modTime = WireCodec.ReadVarLong(s, 4);
        if (xflags.HasFlag(XmitFlags.ModNsec))
            WireCodec.ReadVarInt(s); // nanosecond component -- not tracked in this v1, discarded
        if (!xflags.HasFlag(XmitFlags.SameMode))
            _mode = UnixMode.FromWireMode(WireCodec.ReadInt32(s));
        // Same-uid/Same-gid bits can only be set when the field is actually transmitted;
        // a real peer never sets them under preserve-off (it never even enters the branch
        // that sets them), so clear them defensively before the flags-gated reads.
        if (xflags.HasFlag(XmitFlags.SameUid) && !preserveUid)
            xflags &= ~XmitFlags.SameUid;
        if (xflags.HasFlag(XmitFlags.SameGid) && !preserveGid)
            xflags &= ~XmitFlags.SameGid;
        if (preserveUid && !xflags.HasFlag(XmitFlags.SameUid))
            _uid = WireCodec.ReadVarInt(s);
        if (preserveGid && !xflags.HasFlag(XmitFlags.SameGid))
            _gid = WireCodec.ReadVarInt(s);

        var fileType = UnixMode.ToFileType(_mode);
        string? symlinkTarget = null;
        if (fileType == RsyncFileType.Symlink)
        {
            int targetLen = WireCodec.ReadVarInt(s);
            var targetBytes = new byte[targetLen];
            s.ReadExactly(targetBytes);
            symlinkTarget = Encoding.UTF8.GetString(targetBytes);
        }

        return new FileEntry
        {
            Path = path,
            FileType = fileType,
            Mode = _mode,
            Length = length,
            ModTimeUnix = _modTime,
            Uid = _uid,
            Gid = _gid,
            SymlinkTarget = symlinkTarget,
        };
    }
}
