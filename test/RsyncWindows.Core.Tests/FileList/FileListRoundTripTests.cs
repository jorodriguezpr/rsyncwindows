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

public class FileListRoundTripTests
{
    private static List<FileEntry?> RoundTrip(IEnumerable<FileEntry> entries, bool preserveUid = true, bool preserveGid = true)
    {
        var encoder = new FileListEncoder();
        using var ms = new MemoryStream();
        var list = entries.ToList();
        foreach (var e in list)
            encoder.Write(ms, e, preserveUid, preserveGid);
        encoder.WriteEndOfList(ms);

        ms.Position = 0;
        var decoder = new FileListDecoder();
        var result = new List<FileEntry?>();
        while (true)
        {
            var e = decoder.Read(ms, preserveUid, preserveGid);
            if (e is null)
                break;
            result.Add(e);
        }
        return result;
    }

    private static void AssertEntryEqual(FileEntry expected, FileEntry actual)
    {
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.FileType, actual.FileType);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.ModTimeUnix, actual.ModTimeUnix);
        Assert.Equal(expected.Uid, actual.Uid);
        Assert.Equal(expected.Gid, actual.Gid);
        Assert.Equal(expected.SymlinkTarget, actual.SymlinkTarget);
    }

    [Fact]
    public void SingleRegularFile_RoundTrips()
    {
        var entry = new FileEntry
        {
            Path = "file.txt",
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = 12345,
            ModTimeUnix = 1_700_000_000,
            Uid = 1000,
            Gid = 1000,
        };
        var result = RoundTrip([entry]);
        AssertEntryEqual(entry, Assert.Single(result)!);
    }

    [Fact]
    public void MixedTree_RoundTrips_IncludingSharedPrefixCompression()
    {
        // Deliberately shares long common prefixes across entries so the SAME_NAME /
        // SAME_MODE / SAME_UID / SAME_GID / SAME_TIME diff bits actually get exercised,
        // not just the "always send everything" first-entry path.
        var entries = new List<FileEntry>
        {
            new() { Path = "docs", FileType = RsyncFileType.Directory, Mode = UnixMode.DirectoryDefault, ModTimeUnix = 100, Uid = 1000, Gid = 1000 },
            new() { Path = "docs/readme.txt", FileType = RsyncFileType.Regular, Mode = UnixMode.RegularFileDefault, Length = 42, ModTimeUnix = 100, Uid = 1000, Gid = 1000 },
            new() { Path = "docs/readme2.txt", FileType = RsyncFileType.Regular, Mode = UnixMode.RegularFileDefault, Length = 99, ModTimeUnix = 200, Uid = 1000, Gid = 1000 },
            new() { Path = "docs/sub", FileType = RsyncFileType.Directory, Mode = UnixMode.DirectoryDefault, ModTimeUnix = 100, Uid = 2000, Gid = 3000 },
            new() { Path = "docs/sub/link", FileType = RsyncFileType.Symlink, Mode = UnixMode.SymlinkDefault, ModTimeUnix = 100, Uid = 2000, Gid = 3000, SymlinkTarget = "../readme.txt" },
        };

        var result = RoundTrip(entries);
        Assert.Equal(entries.Count, result.Count);
        for (int i = 0; i < entries.Count; i++)
            AssertEntryEqual(entries[i], result[i]!);
    }

    [Fact]
    public void LongFileName_OverByteThreshold_RoundTrips()
    {
        // Forces XMIT_LONG_NAME (suffix > 255 bytes).
        var longName = new string('a', 300) + ".txt";
        var entry = new FileEntry
        {
            Path = longName,
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = 1,
            ModTimeUnix = 1,
        };
        var result = RoundTrip([entry]);
        AssertEntryEqual(entry, Assert.Single(result)!);
    }

    [Fact]
    public void EmptyList_ProducesNoEntries()
    {
        Assert.Empty(RoundTrip([]));
    }

    [Fact]
    public void RealDirectoryTree_WalkedAndRoundTripped()
    {
        string root = Directory.CreateTempSubdirectory("rsyncwin-flist-test-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "world, a slightly longer file");

            var entries = FileListBuilder.Walk(root).ToList();
            Assert.Equal(3, entries.Count); // sub/ , a.txt, sub/b.txt (some order)

            var result = RoundTrip(entries);
            Assert.Equal(entries.Count, result.Count);
            for (int i = 0; i < entries.Count; i++)
                AssertEntryEqual(entries[i], result[i]!);

            var byPath = result.ToDictionary(e => e!.Path);
            Assert.Equal(RsyncFileType.Directory, byPath["sub"]!.FileType);
            Assert.Equal(RsyncFileType.Regular, byPath["a.txt"]!.FileType);
            Assert.Equal(5, byPath["a.txt"]!.Length);
            Assert.Equal(RsyncFileType.Regular, byPath["sub/b.txt"]!.FileType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreserveOff_OmitsUidGid_FieldsRoundTripWithoutThem()
    {
        // The regression that produced BOTH the Phase 5 daemon-PULL desync and the Phase 6
        // -z push desync: with -o/-g absent (plain `rsync -v`, what every real interop test
        // uses), the encoder must NOT write uid/gid varints -- a real peer's
        // recv_file_entry (flist.c:1000/1011) only consumes those fields when
        // preserve_uid/preserve_gid are on, so extra bytes desync the end-of-flist and
        // every subsequent exchange ("got transfer request in phase 2").
        var entry = new FileEntry
        {
            Path = "plain.txt",
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = 7,
            ModTimeUnix = 555,
            Uid = 1000, // present on the entry, but must NOT be serialized
            Gid = 1000,
        };
        // Byte-exactness assertion: without uid/gid the encoded entry is short enough that
        // the round trip succeeds AND the decoder never touches the omitted fields.
        var result = RoundTrip([entry], preserveUid: false, preserveGid: false);
        var decoded = Assert.Single(result)!;
        Assert.Equal(entry.Path, decoded.Path);
        Assert.Equal(entry.Length, decoded.Length);
        Assert.Equal(entry.ModTimeUnix, decoded.ModTimeUnix);
    }

    [Fact]
    public void PreserveOff_EncodedSize_IsStrictlySmaller_ThanPreserveOn()
    {
        var entry = new FileEntry
        {
            Path = "size.txt",
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = 7,
            ModTimeUnix = 555,
            Uid = 1000,
            Gid = 1000,
        };
        static int Encoded(FileListEncoder encoder, FileEntry e, bool uid, bool gid)
        {
            using var ms = new MemoryStream();
            encoder.Write(ms, e, uid, gid);
            encoder.WriteEndOfList(ms);
            return (int)ms.Length;
        }
        var on = new FileListEncoder();
        var off = new FileListEncoder();
        Assert.True(Encoded(off, entry, false, false) < Encoded(on, entry, true, true),
            "uid/gid varints must be omitted entirely when preserve flags are off");
    }
}
