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
/// Real rsync's actual generator/sender ndx numbering is NOT "array position in the order
/// entries were walked/transmitted" and NOT a plain sorted-by-full-path-name ordinal compare
/// either: it is each entry's position in the flist AFTER fsort(), which sorts with
/// f_name_cmp() (flist.c:3554) -- a component-wise state machine with these observable rules
/// (all confirmed against the real rsync 3.5.0 binary via --debug=FLIST4 --list-only
/// --no-inc-recursive dumps; each probe's actual i=N order is quoted):
///
/// 1. At the SAME directory level, ALL non-directories sort BEFORE ALL directories,
/// regardless of name: "aa_file, mid.txt, zz_file, aa_dir/, zz_dir/". The type check
/// (t_ITEM vs t_PATH) fires at comparison INIT, before any character is examined. ("Bug 8"
/// note: this is exactly why the earlier "compare path with virtual trailing slash"
/// approximation kept failing -- it interleaved dirs among files by name, so a real peer's
/// ndx requests landed on entries one or two slots off from ours.)
///
/// 2. A directory's descendants sort IMMEDIATELY after the directory itself, before any
/// later sibling at the parent level -- because each descendant's DIRNAME is the
/// directory's full path and f_name_cmp walks the dirname component chain before reaching
/// the basename: gooddir/, gooddir/inner1.txt, gooddir/nested/, gooddir/nested/inner2.txt
/// all group together, and everything inside "p" sorts after p/ itself.
///
/// 3. Within the same type at the same level, plain byte-ordinal order of the basename --
/// but a directory's own name carries the virtual trailing '/' ONLY once the comparison
/// has descended past its shared dirname chain (state s_DIR/s_SLASH), e.g. the file
/// "gooddir.bak" vs the DIR "gooddir": at init, file=t_ITEM vs dir=t_PATH and the FILE
/// sorts first on the type check alone; but "gooddir/inner1.txt" (dirname "gooddir",
/// t_PATH) vs "gooddir.bak" (t_ITEM) also puts the file first. The genuinely
/// dirname-mediated edge only appears between two dir-subtree entries or a dir entry and
/// its parent's other descendants, which is what the trailing '/' exists for.
///
/// The exact C semantics this class ports: init each side from dirname (t_PATH, s_DIR) or
/// basename (dir = t_PATH+s_BASE, file = t_ITEM+s_BASE, "." = t_ITEM+s_TRAILING+""); if the
/// types differ, the t_PATH side sorts after; then walk both cursors, transitioning
/// s_DIR -> s_SLASH("/"), s_SLASH -> basename (re-init type/state), s_BASE -> s_TRAILING
/// (a dir's trailing '/' continues with "/"), s_TRAILING -> t_ITEM; compare one char per
/// loop iteration; the side that exhausts first while the other still has type-checkable
/// content wins by type, and a t_TRAILING-vs-t_TRAILING simultaneous exhaustion is equal.
/// </summary>
public static class RsyncFileOrder
{
    public static readonly IComparer<FileEntry> Comparer =
        System.Collections.Generic.Comparer<FileEntry>.Create(Compare);

    // f_name_cmp's state machine (flist.c:3534-3535)
    private enum FncState { Dir, Slash, Base, Trailing }
    private enum FncType { Path, Item }

    private sealed class Cursor
    {
        public required string? Dirname;
        public required string Basename;
        public required bool IsDir;
        public FncType Type;
        public FncState State;
        public string Current = "";

        public static Cursor Create(string path, bool isDir)
        {
            string? dirname;
            string basename;
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                dirname = path[..lastSlash];
                basename = path[(lastSlash + 1)..];
                if (dirname.Length == 0)
                    dirname = null;
            }
            else
            {
                dirname = null;
                basename = path;
            }

            var c = new Cursor { Dirname = dirname, Basename = basename, IsDir = isDir };
            if (dirname != null)
            {
                c.Type = FncType.Path;
                c.State = FncState.Dir;
                c.Current = dirname;
            }
            else
            {
                c.Type = isDir ? FncType.Path : FncType.Item;
                if (c.Type == FncType.Path && basename == ".")
                {
                    c.Type = FncType.Item;
                    c.State = FncState.Trailing;
                    c.Current = "";
                }
                else
                {
                    c.State = FncState.Base;
                    c.Current = basename;
                }
            }
            return c;
        }
    }

    public static int Compare(FileEntry a, FileEntry b)
    {
        var c1 = Cursor.Create(a.Path, a.FileType == RsyncFileType.Directory);
        var c2 = Cursor.Create(b.Path, b.FileType == RsyncFileType.Directory);

        if (c1.Type != c2.Type)
            return c1.Type == FncType.Path ? 1 : -1;

        while (true)
        {
            if (c1.Current.Length == 0)
                Advance(c1);
            if (c2.Current.Length == 0)
                Advance(c2);

            if (c1.Type != c2.Type)
                return c1.Type == FncType.Path ? 1 : -1;

            if (c1.Current.Length == 0 && c2.Current.Length == 0)
            {
                // Both exhausted at once: in the C code both cursors are in s_TRAILING (or
                // degenerate "."-dirs) here; equal names compare equal.
                return 0;
            }
            if (c1.Current.Length == 0)
                return -1;
            if (c2.Current.Length == 0)
                return 1;

            int b1 = c1.Current[0];
            int b2 = c2.Current[0];
            if (b1 != b2)
                return b1 - b2;
            c1.Current = c1.Current[1..];
            c2.Current = c2.Current[1..];
        }
    }

    // One transition of the C do-loop's "if (!*cN)" state machine. A dir's s_BASE ->
    // s_TRAILING keeps emitting '/' (the virtual trailing slash) before giving up to t_ITEM.
    private static void Advance(Cursor c)
    {
        switch (c.State)
        {
            case FncState.Dir:
                c.State = FncState.Slash;
                c.Current = "/";
                break;
            case FncState.Slash:
                c.Type = c.IsDir ? FncType.Path : FncType.Item;
                if (c.Type == FncType.Path && c.Basename == ".")
                {
                    c.Type = FncType.Item;
                    c.State = FncState.Trailing;
                    c.Current = "";
                }
                else
                {
                    c.State = FncState.Base;
                    c.Current = c.Basename;
                }
                break;
            case FncState.Base:
                c.State = FncState.Trailing;
                if (c.Type == FncType.Path)
                    c.Current = "/"; // dir's virtual trailing slash
                else
                    c.Type = FncType.Item;
                break;
            case FncState.Trailing:
                c.Type = FncType.Item;
                break;
        }
    }

    /// <summary>Sorts a decoded/walked entry list into the same order a real rsync peer's own
    /// ndx numbering assumes. Stable (OrderBy-based, not in-place) so callers that also need
    /// the original decode/walk order keep their own copy safe.</summary>
    public static List<FileEntry> Sort(IEnumerable<FileEntry> entries) =>
        entries.OrderBy(e => e, Comparer).ToList();
}
