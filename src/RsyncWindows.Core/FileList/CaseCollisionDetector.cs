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
/// NTFS is case-insensitive-but-preserving: two Unix source paths differing only by case (e.g.
/// `File.txt` and `file.txt` in the same directory -- perfectly valid as distinct files on the
/// sending Linux side) cannot both exist as separate files at the destination. Left unchecked,
/// writing both would silently last-writer-wins clobber one with the other's content -- exactly
/// the plan's own flagged risk: "detect and error, don't silently last-writer-wins."
///
/// Pure string comparison over the flist's own wire paths, no disk access -- safe to run
/// unconditionally as soon as the full file list is known, before any file is written.
/// </summary>
public static class CaseCollisionDetector
{
    /// <summary>Throws <see cref="InvalidOperationException"/> naming every colliding group if
    /// any two (or more) entries' paths are equal under <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// but not under <see cref="StringComparer.Ordinal"/> -- i.e. differ only by case. A no-op
    /// otherwise.</summary>
    public static void ThrowIfCollisions(IReadOnlyList<FileEntry> entries)
    {
        var byCaseInsensitive = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!byCaseInsensitive.TryGetValue(e.Path, out var distinctCasings))
                byCaseInsensitive[e.Path] = distinctCasings = [];
            if (!distinctCasings.Contains(e.Path, StringComparer.Ordinal))
                distinctCasings.Add(e.Path);
        }

        var collisions = byCaseInsensitive.Values.Where(g => g.Count > 1).ToList();
        if (collisions.Count == 0)
            return;

        string details = string.Join("; ", collisions.Select(g => string.Join(" vs ", g)));
        throw new InvalidOperationException(
            $"refusing to write: source path(s) differ only by case, which this destination filesystem cannot represent as distinct files: {details}");
    }
}
