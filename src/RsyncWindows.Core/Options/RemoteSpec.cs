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

using System.Text.RegularExpressions;

namespace RsyncWindows.Core.Options;

public abstract record RemoteSpec
{
    public sealed record Local(string Path) : RemoteSpec;
    public sealed record Ssh(string? User, string Host, string Path) : RemoteSpec;
    public sealed record Daemon(string? User, string Host, int? Port, string Module, string Path) : RemoteSpec;

    /// <summary>
    /// Classifies one SRC/DEST token the way real rsync does: "rsync://host[:port]/module/path",
    /// "host::module/path", "user@host:path" are remote; anything else is local.
    ///
    /// The one thing real rsync (Unix-only) never has to worry about: a Windows absolute path
    /// like "C:\Users\x" or "C:/Users/x" also contains a colon right after what looks like a
    /// one-character "host". A bare single-letter segment before a lone ':' (not '::') is
    /// always treated as a drive letter, never a hostname -- real single-letter rsync hostnames
    /// exist in theory but colliding with every Windows absolute path is far worse.
    /// </summary>
    public static RemoteSpec Parse(string token)
    {
        if (token.StartsWith("rsync://", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(token, @"^rsync://(?:([^@/]+)@)?([^/:]+)(?::(\d+))?/([^/]+)(?:/(.*))?$");
            if (!m.Success)
                throw new RsyncArgException($"malformed rsync:// spec: {token}");
            string? user = m.Groups[1].Success ? m.Groups[1].Value : null;
            string host = m.Groups[2].Value;
            int? port = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null;
            string module = m.Groups[4].Value;
            string path = m.Groups[5].Success ? m.Groups[5].Value : "";
            return new Daemon(user, host, port, module, path);
        }

        int dcolon = token.IndexOf("::", StringComparison.Ordinal);
        if (dcolon >= 0)
        {
            string hostPart = token[..dcolon];
            string rest = token[(dcolon + 2)..];
            var (user, host) = SplitUser(hostPart);
            int slash = rest.IndexOf('/');
            string module = slash >= 0 ? rest[..slash] : rest;
            string path = slash >= 0 ? rest[(slash + 1)..] : "";
            return new Daemon(user, host, null, module, path);
        }

        int colon = token.IndexOf(':');
        if (colon > 0)
        {
            string hostPart = token[..colon];
            string pathPart = token[(colon + 1)..];
            var (_, hostOnly) = SplitUser(hostPart);

            bool looksLikeDriveLetter = hostOnly.Length == 1 && char.IsAsciiLetter(hostOnly[0]);
            if (!looksLikeDriveLetter)
            {
                var (user, host) = SplitUser(hostPart);
                return new Ssh(user, host, pathPart);
            }
        }

        return new Local(token);
    }

    private static (string? User, string Host) SplitUser(string hostPart)
    {
        int at = hostPart.IndexOf('@');
        return at >= 0 ? (hostPart[..at], hostPart[(at + 1)..]) : (null, hostPart);
    }
}
