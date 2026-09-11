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
/// Shared mtime conversion/application so every write path (CLI client, `--server` role, daemon
/// receiver) truncates and applies timestamps identically -- the plan's own flagged risk:
/// "NTFS 100ns ticks vs. rsync's second-or-finer granularity can cause spurious 'changed'
/// detection on repeat syncs if truncation isn't applied consistently both sides." `ModTimeUnix`
/// is always whole Unix seconds on the wire (matches real rsync's own flist field), so the only
/// real risk is inconsistent rounding on the WRITE side -- centralizing it here removes that.
/// </summary>
public static class TimestampSupport
{
    public static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Sets a file's last-write time from a whole-Unix-seconds value, matching what
    /// <see cref="ToUnixSeconds"/> produces on the READ side -- so a round trip through both
    /// (write here, later re-walk via <see cref="FileListBuilder"/>) reproduces the exact same
    /// integer, with no drift. Best-effort: swallows the exception and leaves the file's
    /// existing timestamp alone on failure (e.g. no write access to the metadata), matching this
    /// project's v1 policy of never failing a whole transfer over a non-data attribute.</summary>
    public static void TrySetModTimeUtc(string path, long unixSeconds)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort attribute application, non-fatal per v1 scope
        }
    }
}
