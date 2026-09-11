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
using System.Text.RegularExpressions;

namespace RsyncWindows.Core.FileList;

/// <summary>
/// A deliberately-scoped subset of rsync's exclude/include filtering (exclude.c, 1997 lines;
/// lib/wildmatch.c, 381 lines, for the actual glob algorithm). Supports the patterns people
/// actually write day to day: "*" (any characters except '/'), "**" (any characters,
/// including '/'), "?" (one character except '/'), literal text, and a leading '/' anchoring
/// the pattern to the transfer root instead of matching at any directory depth.
///
/// NOT implemented (a conscious v1 gap, not an oversight): character classes ("[abc]",
/// negated with '!'/'^'), backslash escaping, and rsync's full filter-rule grammar (merge
/// files, perishable/directory-only modifiers, per-rule anchoring flags beyond a leading '/').
/// Rule precedence here is the simple, common-case model: the first pattern (across excludes
/// then includes, in the order supplied) that matches a path decides its fate; an include
/// pattern matching a path that an earlier exclude also matched wins if it was registered
/// first -- callers should add include patterns before the excludes they're meant to override,
/// mirroring how rsync evaluates its filter list top-to-bottom.
/// </summary>
public sealed class FilterEngine
{
    private enum Kind { Exclude, Include }
    private readonly List<(Kind Kind, Regex Pattern)> _rules = [];

    public void AddExclude(string pattern) => _rules.Add((Kind.Exclude, Compile(pattern)));
    public void AddInclude(string pattern) => _rules.Add((Kind.Include, Compile(pattern)));

    /// <returns>True if <paramref name="relativePath"/> (forward-slash separated, no leading
    /// slash) should be transferred.</returns>
    public bool IsIncluded(string relativePath)
    {
        foreach (var (kind, regex) in _rules)
        {
            if (regex.IsMatch(relativePath))
                return kind == Kind.Include;
        }
        return true; // no rule matched -- rsync's default is to include
    }

    private static Regex Compile(string pattern)
    {
        bool anchored = pattern.StartsWith('/');
        if (anchored)
            pattern = pattern[1..];

        var sb = new StringBuilder();
        sb.Append(anchored ? "^" : "^(?:.*/)?");

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append("(?:/.*)?$"); // a match on a directory also covers everything beneath it

        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
