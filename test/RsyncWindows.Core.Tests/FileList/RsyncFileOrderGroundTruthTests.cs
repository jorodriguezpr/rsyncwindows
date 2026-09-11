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

using System.Reflection;
using RsyncWindows.Core.FileList;
using Xunit.Abstractions;

namespace RsyncWindows.Core.Tests.FileList;

/// <summary>
/// Ground-truth vectors captured from the REAL rsync 3.5.0 binary itself
/// (`rsync --debug=FLIST4 -r --list-only --no-inc-recursive <tree>`; each "i=N" line is one
/// sorted-flist entry at its actual ndx). These pin RsyncFileOrder's comparator against
/// rsync's f_name_cmp() state machine, not just an approximation of it. Both trees were
/// captured live (see the FLIST4 dumps in RsyncFileOrder's doc comment): the ordering rule
/// is all-FILES-before-all-DIRS at each level, with each dir's descendants grouped
/// immediately after it, and root-level names ordered by plain ordinal.
/// </summary>
public class RsyncFileOrderGroundTruthTests
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
    public void GroundTruth_MixedTree_MatchesRealRsyncSortExactly()
    {
        // Captured verbatim from real rsync 3.5.0 (probe sp16): all root-level files first
        // (ordinal among themselves), then each dir subtree grouped right after its dir.
        var walkOrder = new List<FileEntry>
        {
            Dir("."),
            File("aaa-before.txt"),
            File("zzz-after.txt"),
            File("emptydir_marker.txt"),
            File("gooddir.bak"),
            Dir("emptydir"),
            File("emptydir/keepme.txt"),
            Dir("gooddir"),
            File("gooddir/inner1.txt"),
            Dir("gooddir/nested"),
            File("gooddir/nested/inner2.txt"),
        };

        var sorted = RsyncFileOrder.Sort(walkOrder);

        Assert.Equal(
        [
            ".",                          // i=0
            "aaa-before.txt",             // i=1
            "emptydir_marker.txt",        // i=2 (files first, ordinal)
            "gooddir.bak",                // i=3
            "zzz-after.txt",              // i=4
            "emptydir",                   // i=5 (dir subtree begins)
            "emptydir/keepme.txt",        // i=6 (children immediately after parent)
            "gooddir",                    // i=7
            "gooddir/inner1.txt",         // i=8
            "gooddir/nested",             // i=9
            "gooddir/nested/inner2.txt",  // i=10
        ], sorted.Select(e => e.Path));
    }

    [Fact]
    public void GroundTruth_DeepTree_MatchesRealRsyncSortExactly()
    {
        var walkOrder = new List<FileEntry>
        {
            Dir("."),
            File("z.txt"),
            File("a.txt"),
            Dir("sub2"),
            Dir("sub1"),
            File("sub2/deep/f2.txt"),
            Dir("sub2/deep"),
            File("sub1/f1.txt"),
        };

        var sorted = RsyncFileOrder.Sort(walkOrder);

        Assert.Equal(
        [
            ".",
            "a.txt",
            "z.txt",
            "sub1",
            "sub1/f1.txt",
            "sub2",
            "sub2/deep",
            "sub2/deep/f2.txt",
        ], sorted.Select(e => e.Path));
    }

    [Fact]
    public void GroundTruth_NestedUnderParent_MatchesRealRsyncSortExactly()
    {
        // Probe sp14 (sah-build shape): the dir sits between two sibling files in WALK
        // order, but its subtree must group immediately after it in SORTED order.
        var walkOrder = new List<FileEntry>
        {
            Dir("."),
            Dir("p"),
            File("p/aaa-before.txt"),
            File("p/zzz-after.txt"),
            File("p/gooddir.bak"),
            Dir("p/gooddir"),
            File("p/gooddir/inner1.txt"),
            Dir("p/gooddir/nested"),
            File("p/gooddir/nested/inner2.txt"),
        };

        var sorted = RsyncFileOrder.Sort(walkOrder);

        Assert.Equal(
        [
            ".",
            "p",
            "p/aaa-before.txt",
            "p/gooddir.bak",
            "p/zzz-after.txt",
            "p/gooddir",
            "p/gooddir/inner1.txt",
            "p/gooddir/nested",
            "p/gooddir/nested/inner2.txt",
        ], sorted.Select(e => e.Path));
    }
}