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

public class RsyncFileOrderTests
{
    private static FileEntry Dir(string path) => new()
    {
        Path = path,
        FileType = RsyncFileType.Directory,
        Mode = UnixMode.DirectoryDefault,
        ModTimeUnix = 0,
    };

    private static FileEntry File(string path) => new()
    {
        Path = path,
        FileType = RsyncFileType.Regular,
        Mode = UnixMode.RegularFileDefault,
        ModTimeUnix = 0,
    };

    [Fact]
    public void Sort_DirectoryNotLastSibling_ReordersChildrenImmediatelyAfterParent()
    {
        // The exact tree shape that desynced against a real rsync client ("Bug 8"): a
        // directory sits BETWEEN two sibling files in walk/wire order. Real rsync's numbering
        // (verified via FLIST4, probe sp16/sp14) puts ALL root-level files first (ordinal
        // among themselves), then each dir subtree grouped immediately after its dir.
        var walkOrder = new List<FileEntry>
        {
            Dir("."),
            File("aaa-before.txt"),
            Dir("gooddir"),
            File("zzz-after.txt"),
            Dir("gooddir/nested"),
            File("gooddir/nested/inner.txt"),
        };

        var sorted = RsyncFileOrder.Sort(walkOrder);

        Assert.Equal(
        [
            ".",
            "aaa-before.txt",
            "zzz-after.txt",           // files first, ordinal: 'a' < 'z'
            "gooddir",                 // then the dir subtree, grouped
            "gooddir/nested",
            "gooddir/nested/inner.txt",
        ], sorted.Select(e => e.Path));
    }

    [Fact]
    public void Sort_AlreadyInOrder_IsUnchanged()
    {
        var walkOrder = new List<FileEntry> { Dir("."), File("zzz.txt"), File("aaa.txt"), Dir("gooddir"), File("gooddir/inner.txt") };
        var sorted = RsyncFileOrder.Sort(walkOrder);

        // Files before dirs (real rsync's type rule), ordinal among each group.
        Assert.Equal([".", "aaa.txt", "zzz.txt", "gooddir", "gooddir/inner.txt"], sorted.Select(e => e.Path));
    }

    [Fact]
    public void Sort_DirectoryVsSimilarlyNamedFile_MatchesRealRsyncsVirtualTrailingSlashRule()
    {
        // f_name_cmp() (flist.c) compares a directory's name as if it had a trailing '/' --
        // "gooddir.bak" (a file) sorts BEFORE "gooddir" (a directory) because '.' (0x2E) is
        // less than '/' (0x2F) at the position right after "gooddir", even though a naive
        // string compare would say the shorter "gooddir" prefix sorts first.
        var entries = new List<FileEntry> { Dir("gooddir"), File("gooddir.bak") };
        var sorted = RsyncFileOrder.Sort(entries);

        Assert.Equal(["gooddir.bak", "gooddir"], sorted.Select(e => e.Path));
    }

    [Fact]
    public void Sort_RootDotDirectory_AlwaysSortsFirst()
    {
        var entries = new List<FileEntry> { File("aaa.txt"), Dir(".") };
        var sorted = RsyncFileOrder.Sort(entries);
        Assert.Equal([".", "aaa.txt"], sorted.Select(e => e.Path));
    }
}
