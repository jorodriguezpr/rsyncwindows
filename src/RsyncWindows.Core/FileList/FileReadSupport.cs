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
/// Best-effort file reads, the read-side counterpart of <see cref="FileWriteSupport"/>: one file
/// that can't be read must not abort the whole transfer.
///
/// Before this, every read was a bare <c>File.ReadAllBytes</c>. Two real failure modes on Windows:
/// <list type="bullet">
/// <item>Delta basis: when the destination file already exists, the receiver reads it to build
/// block checksums. A destination file the current user can't read (ACL deny, a file created by
/// another account) threw out of the receive loop and killed the connection --
/// reported live as "rsyncWindows: Access to the path '\\?\I:\...\config.php' is denied."
/// Real rsync treats an unreadable basis as "no basis" and sends the whole file.</item>
/// <item>Source files: a locked or permission-denied source file aborted a push the same way.
/// Real rsync reports the file and transfers the rest.</item>
/// </list>
/// Files are opened with <c>FileShare.ReadWrite | FileShare.Delete</c>, so a file another program
/// has open for writing (an editor, IIS, a running PHP process) can still be read --
/// <c>File.ReadAllBytes</c> only allows other readers and fails with "being used by another process".
/// </summary>
public static class FileReadSupport
{
    /// <summary>Reads the whole file, tolerating other processes that have it open.</summary>
    public static bool TryReadFile(string path, out byte[] data, out string? error)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length > Array.MaxLength)
                throw new IOException($"file too large to transfer ({length} bytes)");
            var buffer = new byte[length];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break; // shrank while we read it -- send what was there
                read += n;
            }
            data = read == buffer.Length ? buffer : buffer[..read];
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            data = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The delta basis for <paramref name="path"/>: its content, or an empty basis when
    /// it doesn't exist or can't be read (the sender then transfers the whole file).
    /// <paramref name="error"/> is set only when the file exists but couldn't be read.</summary>
    public static byte[] ReadBasisOrEmpty(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
            return [];
        return TryReadFile(path, out byte[] data, out error) ? data : [];
    }
}
