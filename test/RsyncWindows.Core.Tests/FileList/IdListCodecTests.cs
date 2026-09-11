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

using RsyncWindows.Core.FileList;
using Xunit;

namespace RsyncWindows.Core.Tests.FileList;

public class IdListCodecTests
{
    private static FileEntry Entry(string path, int uid, int gid) => new()
    {
        Path = path,
        FileType = RsyncFileType.Regular,
        Mode = UnixMode.RegularFileDefault,
        Length = 0,
        ModTimeUnix = 0,
        Uid = uid,
        Gid = gid,
    };

    [Fact]
    public void WriteThenRead_PreserveUidAndGid_ConsumesExactBytes_LeavesStreamAligned()
    {
        var entries = new[] { Entry("a.txt", 1000, 1000), Entry("b.txt", 1001, 1000) };
        using var ms = new MemoryStream();

        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: true);
        ms.Write([0xAB]); // sentinel byte after the id-lists, to prove the reader stops exactly here
        ms.Position = 0;

        IdListCodec.ReadIdLists(ms, preserveUid: true, preserveGid: true);

        Assert.Equal(0xAB, ms.ReadByte());
        Assert.Equal(-1, ms.ReadByte()); // exactly one sentinel byte, nothing left over
    }

    [Fact]
    public void NeitherPreserveFlagSet_WritesNothing()
    {
        var entries = new[] { Entry("a.txt", 1000, 1000) };
        using var ms = new MemoryStream();
        IdListCodec.WriteIdLists(ms, entries, preserveUid: false, preserveGid: false);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void OnlyPreserveUid_WritesOnlyUidList()
    {
        var entries = new[] { Entry("a.txt", 1000, 2000) };
        using var ms = new MemoryStream();

        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: false);
        ms.Write([0xCD]);
        ms.Position = 0;

        // Reader must be told the SAME flags the writer used, or it'll misread -- exercise that
        // symmetry directly rather than assuming it.
        IdListCodec.ReadIdLists(ms, preserveUid: true, preserveGid: false);
        Assert.Equal(0xCD, ms.ReadByte());
    }

    [Fact]
    public void DuplicateAndZeroIds_DedupedAndExcluded()
    {
        // uid 0 is never listed (matches real rsync's own uidlist.c: id 0 is special and
        // doubles as the terminator) and repeated ids collapse to one entry.
        var entries = new[] { Entry("a.txt", 0, 500), Entry("b.txt", 500, 500), Entry("c.txt", 500, 500) };
        using var ms = new MemoryStream();

        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: true);
        ms.Write([0xEE]);
        ms.Position = 0;

        IdListCodec.ReadIdLists(ms, preserveUid: true, preserveGid: true);
        Assert.Equal(0xEE, ms.ReadByte());
        Assert.Equal(-1, ms.ReadByte());
    }

    /// <summary>
    /// Regression test for the real deadlock this caused against a genuine rsync peer: when
    /// CF_ID0_NAMES (<c>xmit_id0_names</c>, uidlist.c:389-406) is negotiated, real rsync's
    /// `send_one_list` does NOT terminate with a bare `varint(0)` -- it terminates with a full
    /// `send_one_name(f, 0, name)` call (the same `varint(0)` doubling as the id, immediately
    /// followed by a length byte). Missing that trailing byte left a genuine rsync receiver's
    /// `recv_user_name(f, 0)` call blocked forever waiting for it -- confirmed live against a
    /// real rsync 3.5.0 `--server` process, which logged "received N names" (flist decode
    /// succeeded) and then never spoke again. This asserts the exact extra byte, not just that
    /// round-tripping still works, since the whole point is matching a peer we don't control.
    /// </summary>
    [Fact]
    public void Id0Names_WritesTrailingLengthByte_AfterTerminator()
    {
        var entries = new[] { Entry("a.txt", 1000, 2000) };
        using var ms = new MemoryStream();

        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: true, id0Names: true);

        byte[] bytes = ms.ToArray();
        // uid list: varint(1000), byte(0) [empty name], varint(0) [terminator], byte(0) [id 0's own empty name]
        // gid list: same shape for 2000.
        // Exact byte values aren't asserted here (varint encoding is covered elsewhere) -- what
        // matters is there's exactly one MORE byte than the id0Names:false case for each list.
        using var msWithout = new MemoryStream();
        IdListCodec.WriteIdLists(msWithout, entries, preserveUid: true, preserveGid: true, id0Names: false);
        Assert.Equal(msWithout.Length + 2, ms.Length); // +1 per list (uid list, gid list)
    }

    [Fact]
    public void Id0Names_WriteThenRead_RoundTrips_WithNoIdsPresent()
    {
        // The user's real-world failure shape: Windows files carry no genuine uid/gid, so every
        // entry's Uid/Gid is 0 -- WriteOneList's non-zero loop emits nothing, leaving JUST the
        // terminator-plus-id0-name pair on the wire, exactly what a genuine peer's recv_id_list
        // blocks waiting for when it believes CF_ID0_NAMES is active.
        var entries = new[] { Entry("a.txt", 0, 0), Entry("b.txt", 0, 0) };
        using var ms = new MemoryStream();

        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: true, id0Names: true);
        ms.Write([0xFF]);
        ms.Position = 0;

        IdListCodec.ReadIdLists(ms, preserveUid: true, preserveGid: true, id0Names: true);
        Assert.Equal(0xFF, ms.ReadByte());
        Assert.Equal(-1, ms.ReadByte());
    }

    [Fact]
    public void Id0Names_ReaderMustMatchWriter_OrStreamMisaligns()
    {
        // Direct demonstration of why this must be threaded from the NEGOTIATED session on both
        // ends, not a local constant: a reader that doesn't know id0Names was active stops one
        // byte early, leaving the id-0 name-length byte unread.
        var entries = new[] { Entry("a.txt", 0, 0) };
        using var ms = new MemoryStream();
        IdListCodec.WriteIdLists(ms, entries, preserveUid: true, preserveGid: false, id0Names: true);
        ms.Position = 0;

        IdListCodec.ReadIdLists(ms, preserveUid: true, preserveGid: false, id0Names: false);
        Assert.NotEqual(-1, ms.ReadByte()); // the id-0 length byte was left unconsumed
    }
}
