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

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RsyncWindows.Core.Daemon;

/// <summary>
/// An rsyncd.conf-shaped INI config: `key = value` global settings followed by `[module]`
/// sections, each holding the <see cref="RsyncdModule"/> keys. Deliberately not a general INI
/// library -- just enough to parse the handful of keys this v1 understands, matching the
/// project's existing "hand-rolled, table-driven" preference (see RsyncArgParser's doc comment)
/// over pulling in a generic parsing dependency for a format this simple.
/// </summary>
public sealed class RsyncdConfig
{
    /// <summary>Shared between RsyncWindows.Service (which reads/watches it) and
    /// RsyncWindows.Tray (which offers to open it) -- kept here, not in either exe project, so
    /// the two never need to reference each other just to agree on a path.</summary>
    public static string DefaultConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rsyncWindows", "rsyncd.conf");

    /// <summary>Shared default location for the `secrets file =` this v1's tray-driven "manage
    /// users" editor writes to and every module's auth wires up to -- one shared plaintext
    /// user:password store rather than a per-module file, matching how the starter config's own
    /// commented-out example already pointed here.</summary>
    public static string DefaultSecretsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rsyncWindows", "rsyncd.secrets");

    public IReadOnlyList<RsyncdModule> Modules { get; init; } = [];

    /// <summary>The global `port = N` setting (before any `[module]` section), or 873 --
    /// rsync's IANA-registered default -- when unset.</summary>
    public int Port { get; init; } = 873;

    public RsyncdModule? FindModule(string name) =>
        Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public static RsyncdConfig Load(string path) => Parse(File.ReadAllLines(path));

    /// <summary>Serializes <paramref name="port"/>/<paramref name="modules"/> back out in the
    /// same key=value shape <see cref="Parse"/> reads, and writes it to <paramref name="path"/>.
    /// This is a full regeneration, not an in-place edit -- any hand-written comments in the
    /// existing file (e.g. the starter config's commented-out `[example]` block) are replaced,
    /// not preserved. That's an acceptable tradeoff once a file is being managed through the
    /// tray's GUI editors rather than by hand; <see cref="Parse"/>'s own comment-stripping means
    /// round-tripping comments was never guaranteed anyway. The daemon <c>Worker</c> polls this
    /// file's last-write time once a second and hot-reloads on change -- no service restart
    /// needed after a save.</summary>
    public static void Save(string path, int port, IEnumerable<RsyncdModule> modules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# rsyncWindows daemon config -- rsyncd.conf-shaped.");
        sb.AppendLine("# Edited via the rsyncWindows tray app's Manage Folders/Manage Users dialogs.");
        if (port != 873)
            sb.AppendLine($"port = {port}");
        sb.AppendLine();

        foreach (var m in modules)
        {
            sb.AppendLine($"[{m.Name}]");
            sb.AppendLine($"    path = {m.Path}");
            if (!string.IsNullOrEmpty(m.Comment))
                sb.AppendLine($"    comment = {m.Comment}");
            sb.AppendLine($"    read only = {(m.ReadOnly ? "yes" : "no")}");
            sb.AppendLine($"    list = {(m.List ? "yes" : "no")}");
            if (m.HostsAllow.Count > 0)
                sb.AppendLine($"    hosts allow = {string.Join(", ", m.HostsAllow)}");
            if (m.HostsDeny.Count > 0)
                sb.AppendLine($"    hosts deny = {string.Join(", ", m.HostsDeny)}");
            if (m.AuthUsers.Count > 0)
                sb.AppendLine($"    auth users = {string.Join(", ", m.AuthUsers)}");
            if (!string.IsNullOrEmpty(m.SecretsFile))
                sb.AppendLine($"    secrets file = {m.SecretsFile}");
            if (m.MaxConnections != 0)
                sb.AppendLine($"    max connections = {m.MaxConnections}");
            if (m.TimeoutSeconds != 0)
                sb.AppendLine($"    timeout = {m.TimeoutSeconds}");
            sb.AppendLine();
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }

    public static RsyncdConfig Parse(IEnumerable<string> lines)
    {
        var modules = new List<RsyncdModule>();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (name == null)
                return;
            modules.Add(BuildModule(name, kv));
        }

        foreach (var raw in lines)
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                name = line[1..^1].Trim();
                kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0)
                continue; // not a key=value line -- ignore rather than fail, matching a tolerant config reader
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            (name == null ? globals : kv)[key] = value;
        }
        Flush();

        return new RsyncdConfig { Modules = modules, Port = ParseInt(globals.GetValueOrDefault("port"), 873) };
    }

    private static string StripComment(string line)
    {
        // rsyncd.conf treats both '#' and ';' as comment leaders for a whole line beginning
        // with them; a value is never expected to contain either in this v1's key set, so a
        // simple leading-character check (after trim) is enough without a quoting grammar.
        string trimmed = line.TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '#' || trimmed[0] == ';') ? "" : line;
    }

    private static RsyncdModule BuildModule(string name, Dictionary<string, string> kv)
    {
        return new RsyncdModule
        {
            Name = name,
            Path = kv.GetValueOrDefault("path", ""),
            Comment = kv.GetValueOrDefault("comment", ""),
            ReadOnly = ParseBool(kv.GetValueOrDefault("read only"), true),
            List = ParseBool(kv.GetValueOrDefault("list"), true),
            HostsAllow = ParseList(kv.GetValueOrDefault("hosts allow")),
            HostsDeny = ParseList(kv.GetValueOrDefault("hosts deny")),
            AuthUsers = ParseList(kv.GetValueOrDefault("auth users")),
            SecretsFile = kv.GetValueOrDefault("secrets file"),
            MaxConnections = ParseInt(kv.GetValueOrDefault("max connections"), 0),
            TimeoutSeconds = ParseInt(kv.GetValueOrDefault("timeout"), 0),
        };
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return value.Trim().ToLowerInvariant() is "yes" or "true" or "1";
    }

    private static int ParseInt(string? value, int defaultValue) =>
        !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out int n) ? n : defaultValue;

    private static IReadOnlyList<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Ports the effective allow/deny semantics of `hosts allow`/`hosts deny`
    /// (loadparm/access.c): if `hosts allow` is set, the address must match one of its entries
    /// or the connection is refused; otherwise, a match in `hosts deny` refuses it; with neither
    /// set, every address is allowed. Entries are an exact IP, a `network/prefix-length` CIDR,
    /// or `*` (match-all) -- hostname/wildcard patterns are a deferred v2 concern (this v1 has
    /// no reverse-DNS lookup, see the plan's non-goals).</summary>
    public static bool IsHostAllowed(RsyncdModule module, IPAddress address)
    {
        if (module.HostsAllow.Count > 0)
            return module.HostsAllow.Any(rule => MatchesRule(rule, address));
        if (module.HostsDeny.Count > 0)
            return !module.HostsDeny.Any(rule => MatchesRule(rule, address));
        return true;
    }

    private static bool MatchesRule(string rule, IPAddress address)
    {
        rule = rule.Trim();
        if (rule == "*")
            return true;

        int slash = rule.IndexOf('/');
        if (slash >= 0)
        {
            if (!IPAddress.TryParse(rule[..slash], out var network) || !int.TryParse(rule[(slash + 1)..], out int prefixLen))
                return false;
            return IsInSubnet(address, network, prefixLen);
        }

        return IPAddress.TryParse(rule, out var exact) && exact.Equals(address);
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;
        byte[] addrBytes = address.GetAddressBytes();
        byte[] netBytes = network.GetAddressBytes();
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (addrBytes[i] != netBytes[i])
                return false;
        }
        if (remainingBits == 0)
            return true;
        int mask = (byte)~(0xFF >> remainingBits);
        return fullBytes < addrBytes.Length && (addrBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }
}
