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

namespace RsyncWindows.Core.Options;

/// <summary>
/// Builds the argv real rsync's own do_cmd()/server_options() (main.c:517, options.c:2768)
/// constructs for the remote `--server` process: `--server [--sender] &lt;bundled-short-flags&gt;
/// . &lt;remote-path&gt;`. Getting the bundled-flags string (and especially its mandatory
/// "e32.0fxCIvu"-style suffix) right matters beyond just "the remote parses our options
/// correctly" -- the remote SERVER derives its compat_flags (CF_VARINT_FLIST_FLAGS,
/// CF_CHKSUM_SEED_FIX, etc, see compat.c:722-755) by scanning this exact string for specific
/// letters, and this v1's wire code hard-assumes those flags end up set.
///
/// Scoped to the option subset <see cref="RsyncOptions"/> exposes; the 'i' (incremental
/// recursion) capability letter is deliberately never sent (compat.c:178 turns off
/// allow_inc_recurse on the server when 'i' is absent from client_info) since this v1's file
/// list is always a single complete list, not the chunked multi-flist protocol variant.
/// </summary>
public static class ServerInvocationBuilder
{
    public static IReadOnlyList<string> BuildServerArgs(RsyncOptions options, bool amSender, string remotePath)
    {
        var args = new List<string> { "--server" };
        if (!amSender)
            args.Add("--sender");

        var flags = new StringBuilder("-");
        for (int i = 0; i < Math.Min(options.Verbose, 9); i++)
            flags.Append('v');
        if (options.PreserveLinks) flags.Append('l');
        if (options.Update) flags.Append('u');
        if (options.DryRun) flags.Append('n');
        if (options.PreserveOwner) flags.Append('o');
        if (options.PreserveGroup) flags.Append('g');
        if (options.PreserveDevices) flags.Append('D');
        if (options.PreserveTimes) flags.Append('t');
        if (options.PreservePerms) flags.Append('p');
        if (options.Recursive) flags.Append('r');
        if (options.WholeFileChecksum) flags.Append('c');
        if (options.Relative) flags.Append('R');
        if (options.Compress) flags.Append('z');

        AppendClientInfoSuffix(flags);

        if (flags.Length > 1)
            args.Add(flags.ToString());

        if (options.PreserveDevices && !options.PreserveSpecials)
            args.Add("--no-specials"); // -D implies both; undo the half we don't want
        else if (options.PreserveSpecials && !options.PreserveDevices)
            args.Add("--specials");

        if (options.CompressLevel is { } level)
            args.Add($"--compress-level={level}");
        if (options.BwLimitKBytes is { } bwlimit)
            args.Add($"--bwlimit={bwlimit}");

        args.Add(".");
        // An empty remotePath means "the module/SSH-target's own root, no subpath" (e.g.
        // `host::module/` or `host::module` with nothing after) -- real rsync represents that
        // case as "." (matching Unix's own "current directory" convention), never as a literal
        // empty string. This matters for a reason beyond style: the NUL-terminated argv-list
        // wire encoding (clientserver.c's rl_nulls form, see DaemonHandshake.RunClientSide's own
        // writer) uses an EMPTY string as its own list terminator -- sending a genuinely empty
        // path here is indistinguishable from ending the list one argument early, silently
        // dropping this argument and leaving the real terminator's NUL byte to be misread as the
        // start of whatever the peer reads next (confirmed directly: this exact bug produced
        // "peer's checksum-choice list ('') does not include md5" once the very next read
        // happened to be the checksum-choice vstring, whose length-prefix byte the stray NUL
        // satisfied as "zero-length string").
        args.Add(string.IsNullOrEmpty(remotePath) ? "." : remotePath);
        return args;
    }

    /// <summary>Ports maybe_add_e_option() (options.c:3192) for protocol 32 exactly
    /// (PROTOCOL_VERSION=32, SUBPROTOCOL_VERSION=0): appends "e32.0" then the capability
    /// letters this implementation actually supports -- f (safe end-of-flist marker),
    /// x (no xattr-hardlink optimization needed, we don't do xattrs), C (checksum-seed-order
    /// fix -- CRITICAL, this v1 always assumes proper_seed_order), I (inplace_partial
    /// behavior -- harmless to claim, we don't use --inplace), v (varint flist flags --
    /// CRITICAL, this v1's FileListEncoder/Decoder only implement that path), u (id0 names).
    /// Deliberately omits 'i' (see class doc comment), 'L' and 's' (symlink time-setting /
    /// iconv translation -- not implemented, and compile-time-conditional in real rsync
    /// anyway).</summary>
    private static void AppendClientInfoSuffix(StringBuilder flags)
    {
        flags.Append("e32.0fxCIvu");
    }
}
