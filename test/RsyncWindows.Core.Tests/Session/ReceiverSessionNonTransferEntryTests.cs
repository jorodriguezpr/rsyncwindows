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
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Session;

/// <summary>End-to-end (real <see cref="SenderSession"/> + <see cref="ReceiverSession"/> over a
/// loopback transport, not hand-built wire bytes) coverage for
/// <see cref="ReceiverSession.RunReceiverLoop"/>'s `onNonTransferEntry` callback -- the hook
/// that lets a caller actually create directories/symlinks on disk (see
/// <see cref="RsyncWindows.Core.FileList.SymlinkSupport"/>), since Directory/Symlink flist
/// entries never go through a transfer request of their own.</summary>
public class ReceiverSessionNonTransferEntryTests
{
    [Fact]
    public async Task DirectoryAndSymlinkEntries_InvokeCallback_RegularFileDoesNot()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();

        var dirEntry = new FileEntry
        {
            Path = "emptydir",
            FileType = RsyncFileType.Directory,
            Mode = UnixMode.DirectoryDefault,
            ModTimeUnix = 0,
        };
        var linkEntry = new FileEntry
        {
            Path = "alink",
            FileType = RsyncFileType.Symlink,
            Mode = UnixMode.SymlinkDefault,
            SymlinkTarget = "somewhere/else.txt",
            ModTimeUnix = 0,
        };
        byte[] fileContent = "hello"u8.ToArray();
        var fileEntry = new FileEntry
        {
            Path = "regular.txt",
            FileType = RsyncFileType.Regular,
            Mode = UnixMode.RegularFileDefault,
            Length = fileContent.Length,
            ModTimeUnix = 0,
        };

        var senderTask = Task.Run(() =>
        {
            // ReceiverSession re-sorts the decoded flist into rsync's ndx order
            // (RsyncFileOrder: files before dirs/symlinks at the same level, ordinal) and
            // indexes files[ndx] by SORTED position -- so the sender-side FileToSend list
            // must be in that same sorted order, not wire order.
            var sorted = RsyncFileOrder.Sort(new[] { dirEntry, linkEntry, fileEntry });
            var encoder = new FileListEncoder();
            foreach (var e in sorted)
                encoder.Write(left.Output, e, preserveUid: false, preserveGid: false);
            encoder.WriteEndOfList(left.Output);
            left.Output.Flush();

            // One FileToSend per flist entry, same sorted order -- SenderSession indexes
            // files[ndx] by the entry's position in the FULL flist (directories/symlinks
            // included), matching how ReceiverSession assigns ndx while walking the sorted
            // entries (see that class's loop).
            var files = sorted.Select(e =>
                e.FileType == RsyncFileType.Regular
                    ? new SenderSession.FileToSend(e, fileContent)
                    : new SenderSession.FileToSend(e, [])).ToList();
            SenderSession.RunSenderLoop(left.Input, left.Output, files, checksumSeed: 0x1234);
            left.Output.Flush();
        });

        var nonTransferEntries = new List<FileEntry>();
        var receiverTask = Task.Run(() => ReceiverSession.RunReceiverLoop(
            right.Input, right.Output, _ => [], checksumSeed: 0x1234,
            onNonTransferEntry: e => { lock (nonTransferEntries) nonTransferEntries.Add(e); }));

        await senderTask;
        var received = await receiverTask;

        Assert.Equal(2, nonTransferEntries.Count);
        Assert.Contains(nonTransferEntries, e => e.Path == "emptydir" && e.FileType == RsyncFileType.Directory);
        Assert.Contains(nonTransferEntries, e => e.Path == "alink" && e.FileType == RsyncFileType.Symlink && e.SymlinkTarget == "somewhere/else.txt");
        Assert.DoesNotContain(nonTransferEntries, e => e.Path == "regular.txt");

        var regular = Assert.Single(received);
        Assert.Equal("regular.txt", regular.Entry.Path);
        Assert.Equal(fileContent, regular.Data);
    }

    [Fact]
    public void FlistWithCaseOnlyCollision_ThrowsBeforeAnyTransferRequest()
    {
        // No loopback/threading needed -- CaseCollisionDetector fires right after the flist is
        // fully read, before the transfer-request loop ever writes anything, so a plain
        // MemoryStream (no peer needed on the other end) is enough to prove it.
        var e1 = new FileEntry { Path = "Notes.txt", FileType = RsyncFileType.Regular, Mode = UnixMode.RegularFileDefault, Length = 1, ModTimeUnix = 0 };
        var e2 = new FileEntry { Path = "notes.txt", FileType = RsyncFileType.Regular, Mode = UnixMode.RegularFileDefault, Length = 1, ModTimeUnix = 0 };

        using var wire = new MemoryStream();
        var encoder = new FileListEncoder();
        encoder.Write(wire, e1, preserveUid: false, preserveGid: false);
        encoder.Write(wire, e2, preserveUid: false, preserveGid: false);
        encoder.WriteEndOfList(wire);
        wire.Position = 0;

        using var discardOutput = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReceiverSession.RunReceiverLoop(wire, discardOutput, _ => [], checksumSeed: 0x1234));
        Assert.Contains("Notes.txt", ex.Message);
        Assert.Contains("notes.txt", ex.Message);
    }
}
