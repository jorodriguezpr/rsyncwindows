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

using System.Text;

namespace RsyncWindows.Core.Daemon;

/// <summary>
/// Reads and writes the plaintext `user:password` lines rsyncd's `secrets file =` module setting
/// points at (real rsync's own secrets.c format -- one colon-separated pair per line, no quoting
/// or escaping). <see cref="DaemonHandshake"/>'s server-side auth already reads this format
/// directly via its own private lookup; this type exists so the tray app's user-management editor
/// can list/add/remove entries without duplicating that parsing. Locking the file down to
/// administrators only (matching secrets.c's own insistence on non-world-readable permissions) is
/// a Windows-ACL concern handled by the caller (the tray's elevated editor), not here -- this
/// class only does the plain read/write, kept platform-agnostic like the rest of Core.
/// </summary>
public static class RsyncdSecretsFile
{
    public sealed record Entry(string User, string Password);

    public static List<Entry> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        var entries = new List<Entry>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            entries.Add(new Entry(line[..colon], line[(colon + 1)..]));
        }
        return entries;
    }

    public static void Save(string path, IEnumerable<Entry> entries)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.Append(e.User).Append(':').Append(e.Password).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }
}
