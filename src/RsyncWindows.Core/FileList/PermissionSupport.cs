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
/// Best-effort Windows mapping of Unix permission bits, per the plan's own documented v1 scope:
/// "Send/receive the real Unix mode/uid/gid faithfully over the wire... but apply only a
/// best-effort subset on the Windows-writing end (e.g. owner-write bit -&gt;
/// `FileAttributes.ReadOnly`); no Windows-SID mapping in v1 -- document as a non-goal, not a
/// silent gap." <see cref="FileEntry.Mode"/> already carries the real bits unchanged for a Linux
/// peer's benefit (see <see cref="UnixMode"/>) -- this class is only about what gets APPLIED
/// locally when writing to NTFS.
/// </summary>
public static class PermissionSupport
{
    private const int OwnerWriteBit = 0x80; // S_IWUSR, octal 0200

    /// <summary>Maps the owner-write bit to the inverse of <see cref="FileAttributes.ReadOnly"/>
    /// -- the one Windows attribute with a direct, unambiguous Unix-permission analog. Swallows
    /// failures (e.g. no write access to the metadata) rather than failing the whole transfer,
    /// matching this project's v1 best-effort-attribute policy.</summary>
    public static void TryApplyReadOnly(string path, int mode)
    {
        try
        {
            bool ownerCanWrite = (mode & OwnerWriteBit) != 0;
            var attrs = File.GetAttributes(path);
            var updated = ownerCanWrite ? (attrs & ~FileAttributes.ReadOnly) : (attrs | FileAttributes.ReadOnly);
            if (updated != attrs)
                File.SetAttributes(path, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort attribute application, non-fatal per v1 scope
        }
    }
}
