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
/// Best-effort directory/file creation for received entries -- mirrors the existing
/// <c>Try*</c> degrade-gracefully convention already used by <see cref="SymlinkSupport"/>,
/// <see cref="TimestampSupport"/>, and <see cref="PermissionSupport"/> (skip and report, don't
/// abort the whole transfer over one bad entry).
///
/// Exists specifically for names that are valid on the SENDING peer's filesystem but illegal on
/// NTFS -- most commonly a colon (':', reserved for NTFS Alternate Data Streams), but also
/// reserved device names ("CON", "NUL", "COM1", ...), trailing dots/spaces, and other characters
/// Windows rejects. A real Linux source tree can genuinely contain such names (colons are
/// completely ordinary on ext4), and before this, hitting even ONE such entry mid-transfer threw
/// an unhandled exception out of the receive loop that killed the ENTIRE connection -- every
/// other file in the same transfer was lost too, not just the one Windows can't represent.
/// Confirmed with a real-world push: a source tree containing a directory literally named "C:"
/// (itself an accidental artifact of some other tool, but a legitimate directory on Linux)
/// closed the connection after `Directory.CreateDirectory` threw
/// "The filename, directory name, or volume label syntax is incorrect" for
/// "...\C:" -- real rsync's own behavior here is to report a per-file IO error and continue
/// transferring everything else, which is what this restores.
/// </summary>
public static class FileWriteSupport
{
    public static bool TryCreateDirectory(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Creates the file's parent directory (if any) and writes its content -- both
    /// steps can fail for the same "illegal on NTFS" reasons, so both are covered by the one
    /// try/catch.</summary>
    public static bool TryWriteFile(string path, byte[] data, out string? error)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, data);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }
}
