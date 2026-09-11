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

public enum RsyncFileType
{
    Regular,
    Directory,
    Symlink,
    // Device/Special deliberately not modeled in v1 -- see the plan's Phase 6 deferral list.
}

/// <summary>
/// Our in-memory representation of one rsync file-list entry. Deliberately narrower than
/// rsync's own file_struct (no ACLs/xattrs/hardlinks/device-node fields, no atime/crtime,
/// mtime at 1-second resolution) -- see FileListEncoder/Decoder's doc comments for exactly
/// which XMIT_* wire features that scope excludes.
/// </summary>
public sealed class FileEntry
{
    /// <summary>Forward-slash-separated path as it appears on the wire (relative to the
    /// transfer root), e.g. "sub/dir/file.txt".</summary>
    public required string Path { get; init; }

    public required RsyncFileType FileType { get; init; }

    /// <summary>Full Unix mode including type bits (e.g. 0100644 for a regular file),
    /// matching what to_wire_mode/from_wire_mode operate on in the C source.</summary>
    public required int Mode { get; init; }

    /// <summary>File size in bytes. Meaningful for regular files; 0 for directories/symlinks
    /// (matching what F_LENGTH would report for those types).</summary>
    public long Length { get; init; }

    /// <summary>Modification time, whole seconds since the Unix epoch.</summary>
    public required long ModTimeUnix { get; init; }

    public int Uid { get; init; }
    public int Gid { get; init; }

    /// <summary>Symlink target, required when <see cref="FileType"/> is Symlink.</summary>
    public string? SymlinkTarget { get; init; }
}
