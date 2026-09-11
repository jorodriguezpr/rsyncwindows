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

namespace RsyncWindows.Core.Options;

/// <summary>
/// Hand-rolled, table-driven parser for rsync's argv grammar (short-flag bundling like
/// "-avz", a short option's value either attached in-bundle like "-e/bin/ssh" or in the next
/// token, "--long=value" or "--long value", "--no-OPT" negation, and a literal "--" ending
/// option parsing) -- deliberately NOT built on a generic .NET CLI-parsing library, since
/// getting this bug-for-bug compatible with real rsync's own option grammar is a stated
/// requirement (see the plan's rationale in RsyncArgParser's design note).
///
/// Covers the ~25-option common set; anything not in the table is rejected with a clear
/// error naming the option, rather than silently ignored.
/// </summary>
public static class RsyncArgParser
{
    private sealed record OptionSpec(bool TakesValue, Action<RsyncOptions, string?> Apply);

    private static readonly Dictionary<string, OptionSpec> LongOptions = new();
    private static readonly Dictionary<char, OptionSpec> ShortOptions = new();

    static RsyncArgParser()
    {
        void Long(string name, bool takesValue, Action<RsyncOptions, string?> apply) => LongOptions[name] = new OptionSpec(takesValue, apply);
        void Short(char c, bool takesValue, Action<RsyncOptions, string?> apply) => ShortOptions[c] = new OptionSpec(takesValue, apply);
        void Both(string name, char c, bool takesValue, Action<RsyncOptions, string?> apply)
        {
            Long(name, takesValue, apply);
            Short(c, takesValue, apply);
        }

        Both("verbose", 'v', false, (o, _) => o.Verbose++);
        Both("dry-run", 'n', false, (o, _) => o.DryRun = true);

        Short('a', false, (o, _) =>
        {
            o.Recursive = o.PreserveLinks = o.PreservePerms = o.PreserveTimes
                = o.PreserveOwner = o.PreserveGroup = o.PreserveDevices = o.PreserveSpecials = true;
        });
        Long("archive", false, ShortOptions['a'].Apply);

        Both("recursive", 'r', false, (o, _) => o.Recursive = true);
        Long("no-recursive", false, (o, _) => o.Recursive = false);
        Both("links", 'l', false, (o, _) => o.PreserveLinks = true);
        Long("no-links", false, (o, _) => o.PreserveLinks = false);
        Both("perms", 'p', false, (o, _) => o.PreservePerms = true);
        Long("no-perms", false, (o, _) => o.PreservePerms = false);
        Both("times", 't', false, (o, _) => o.PreserveTimes = true);
        Long("no-times", false, (o, _) => o.PreserveTimes = false);
        Both("owner", 'o', false, (o, _) => o.PreserveOwner = true);
        Long("no-owner", false, (o, _) => o.PreserveOwner = false);
        Both("group", 'g', false, (o, _) => o.PreserveGroup = true);
        Long("no-group", false, (o, _) => o.PreserveGroup = false);
        Long("devices", false, (o, _) => o.PreserveDevices = true);
        Long("no-devices", false, (o, _) => o.PreserveDevices = false);
        Long("specials", false, (o, _) => o.PreserveSpecials = true);
        Long("no-specials", false, (o, _) => o.PreserveSpecials = false);
        Short('D', false, (o, _) => o.PreserveDevices = o.PreserveSpecials = true);
        Long("no-D", false, (o, _) => o.PreserveDevices = o.PreserveSpecials = false);

        Both("compress", 'z', false, (o, _) => o.Compress = true);
        Long("no-compress", false, (o, _) => o.Compress = false);
        Long("compress-level", true, (o, v) => o.CompressLevel = int.Parse(v!));

        Both("checksum", 'c', false, (o, _) => o.WholeFileChecksum = true);

        Long("delete", false, (o, _) => o.Delete = true);
        Long("del", false, (o, _) => o.Delete = true);
        Long("delete-before", false, (o, _) => { o.Delete = true; o.DeleteTiming = DeleteTiming.Before; });
        Long("delete-during", false, (o, _) => { o.Delete = true; o.DeleteTiming = DeleteTiming.During; });
        Long("delete-delay", false, (o, _) => { o.Delete = true; o.DeleteTiming = DeleteTiming.Delay; });
        Long("delete-after", false, (o, _) => { o.Delete = true; o.DeleteTiming = DeleteTiming.After; });
        Long("delete-excluded", false, (o, _) => { o.Delete = true; o.DeleteExcluded = true; });

        Long("exclude", true, (o, v) => o.ExcludePatterns.Add(v!));
        Long("include", true, (o, v) => o.IncludePatterns.Add(v!));
        Long("exclude-from", true, (o, v) => o.ExcludeFromFiles.Add(v!));
        Long("include-from", true, (o, v) => o.IncludeFromFiles.Add(v!));
        Both("filter", 'f', true, (o, v) => o.FilterRules.Add(v!));
        Long("cvs-exclude", false, (o, _) => { }); // -C: recognized, not yet implemented (no-op in v1)

        Short('P', false, (o, _) => { o.Partial = true; o.Progress = true; });
        Long("partial", false, (o, _) => o.Partial = true);
        Long("partial-dir", true, (o, v) => { o.Partial = true; o.PartialDir = v; });
        Long("progress", false, (o, _) => o.Progress = true);
        Long("stats", false, (o, _) => o.Stats = true);
        Both("human-readable", 'h', false, (o, _) => o.HumanReadable = true);
        Both("itemize-changes", 'i', false, (o, _) => o.ItemizeChanges = true);
        Long("bwlimit", true, (o, v) => o.BwLimitKBytes = ParseBwLimit(v!));

        Both("rsh", 'e', true, (o, v) => o.RemoteShell = v);
        Long("rsync-path", true, (o, v) => o.RsyncPath = v);
        Long("password-file", true, (o, v) => o.PasswordFile = v);

        Both("update", 'u', false, (o, _) => o.Update = true);
        Long("existing", false, (o, _) => o.ExistingOnly = true);
        Long("ignore-existing", false, (o, _) => o.IgnoreExisting = true);
        Both("relative", 'R', false, (o, _) => o.Relative = true);
        Long("no-relative", false, (o, _) => o.Relative = false);

        Long("files-from", true, (o, v) => o.FilesFrom = v);
        Both("backup", 'b', false, (o, _) => o.Backup = true);
        Long("backup-dir", true, (o, v) => { o.Backup = true; o.BackupDir = v; });

        Long("out-format", true, (o, v) => o.OutFormat = v);
        Long("log-file", true, (o, v) => o.LogFile = v);
    }

    private static long ParseBwLimit(string v)
    {
        // rsync's --bwlimit accepts a bare KB/s number or a suffixed rate (e.g. "1.5m", "2g");
        // v1 supports the common suffixes only.
        v = v.Trim();
        char last = char.ToLowerInvariant(v[^1]);
        double multiplier = last switch { 'k' => 1, 'm' => 1024, 'g' => 1024 * 1024, _ => 0 };
        if (multiplier > 0)
            return (long)(double.Parse(v[..^1]) * multiplier);
        return long.Parse(v);
    }

    public static RsyncOptions Parse(IReadOnlyList<string> args)
    {
        var opts = new RsyncOptions();
        var positional = new List<string>();
        bool optionsEnded = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (optionsEnded || arg == "-" || !arg.StartsWith('-'))
            {
                positional.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (arg.StartsWith("--"))
            {
                string name = arg[2..];
                string? inlineValue = null;
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inlineValue = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (!LongOptions.TryGetValue(name, out var spec))
                    throw new RsyncArgException($"unknown option: --{name}");

                string? value = inlineValue;
                if (spec.TakesValue && value is null)
                {
                    if (++i >= args.Count)
                        throw new RsyncArgException($"option --{name} requires a value");
                    value = args[i];
                }
                spec.Apply(opts, value);
                continue;
            }

            // Short-option bundle, e.g. "-avz" or "-e/bin/ssh".
            string bundle = arg[1..];
            for (int c = 0; c < bundle.Length; c++)
            {
                char ch = bundle[c];
                if (!ShortOptions.TryGetValue(ch, out var spec))
                    throw new RsyncArgException($"unknown option: -{ch}");

                if (!spec.TakesValue)
                {
                    spec.Apply(opts, null);
                    continue;
                }

                string value;
                if (c + 1 < bundle.Length)
                {
                    value = bundle[(c + 1)..]; // rest of this bundle is the value, e.g. -e/bin/ssh
                    // Short-option "=value" syntax support: -e=<value> must not keep the '='
                    // as part of the value (real rsync's own popt parsing accepts -e=X the
                    // same as -eX). Without this, SplitRsh saw a program literally named
                    // "=wsl.exe" and failed to spawn.
                    if (value.StartsWith('='))
                        value = value[1..];
                }
                else
                {
                    if (++i >= args.Count)
                        throw new RsyncArgException($"option -{ch} requires a value");
                    value = args[i];
                }
                spec.Apply(opts, value);
                break; // the value consumed the remainder of the bundle
            }
        }

        if (positional.Count == 0)
            throw new RsyncArgException("no source/destination specified");
        opts.Destination = positional[^1];
        opts.Sources.AddRange(positional.Take(positional.Count - 1));
        if (opts.Sources.Count == 0)
            throw new RsyncArgException("at least one source and a destination are required");

        return opts;
    }
}

public sealed class RsyncArgException(string message) : Exception(message);
