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

namespace RsyncWindows.Core.FileList;

/// <summary>
/// Walks a local directory tree into a sequence of <see cref="FileEntry"/> values.
/// Corresponds to rsync's own recursive directory scan (flist.c's send_directory et al.),
/// scoped to v1's regular-file/directory/symlink model -- devices, specials, ACLs, and
/// xattrs are not represented (see UnixMode's doc comment for why mode bits are synthesized
/// rather than read from the filesystem: Windows has no real Unix mode of its own).
///
/// Traversal happens depth-first, per-directory-alphabetical, but the returned sequence is
/// re-sorted into rsync's own GLOBAL flattened-pathname order (<see cref="RsyncFileOrder"/>)
/// before being returned -- a real peer's own ndx numbering is based on THAT sorted order, not
/// on walk/transmission order, and the two genuinely differ whenever a directory isn't the last
/// sibling at its level (see RsyncFileOrder's doc comment for how this was found: "Bug 8",
/// confirmed via a byte-accurate manual wire decode against a real push that desynced).
/// </summary>
public static class FileListBuilder
{
    public static IEnumerable<FileEntry> Walk(string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath);
        return RsyncFileOrder.Sort(WalkRecursive(rootPath, rootPath));
    }

    private static IEnumerable<FileEntry> WalkRecursive(string root, string dir)
    {
        var dirInfo = new DirectoryInfo(dir);
        foreach (var child in dirInfo.EnumerateFileSystemInfos().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');

            if (child.LinkTarget is { } linkTarget)
            {
                // A reparse point -- whether it's a file or directory symlink, treat it as a
                // leaf entry and don't recurse into it (matches rsync's default behavior
                // without -K/--keep-dirlinks or -k/--copy-dirlinks).
                yield return new FileEntry
                {
                    Path = relative,
                    FileType = RsyncFileType.Symlink,
                    Mode = UnixMode.SymlinkDefault,
                    Length = 0,
                    ModTimeUnix = ToUnixSeconds(child.LastWriteTimeUtc),
                    SymlinkTarget = linkTarget,
                };
                continue;
            }

            switch (child)
            {
                case DirectoryInfo d:
                    yield return new FileEntry
                    {
                        Path = relative,
                        FileType = RsyncFileType.Directory,
                        Mode = UnixMode.DirectoryDefault,
                        Length = 0,
                        ModTimeUnix = ToUnixSeconds(d.LastWriteTimeUtc),
                    };
                    foreach (var sub in WalkRecursive(root, d.FullName))
                        yield return sub;
                    break;

                case FileInfo f:
                    yield return new FileEntry
                    {
                        Path = relative,
                        FileType = RsyncFileType.Regular,
                        Mode = UnixMode.RegularFileDefault,
                        Length = f.Length,
                        ModTimeUnix = ToUnixSeconds(f.LastWriteTimeUtc),
                    };
                    break;
            }
        }
    }

    private static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
