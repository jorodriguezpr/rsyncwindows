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
using System.Security.Cryptography;
using System.Text;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.Daemon;

/// <summary>
/// Ports the daemon-mode (`rsync://host/module/path`, historically port 873) inband handshake
/// from clientserver.c's exchange_protocols()/start_daemon()/rsync_module()/start_inband_exchange()
/// plus the challenge/response auth exchange from authenticate.c. This is a text-line-based
/// preamble that runs BEFORE <see cref="Wire.ProtocolNegotiator"/> -- greeting, module selection,
/// optional auth, "OK", then a NUL-separated argv list standing in for what an SSH-piped
/// connection would have passed as real process argv to `--server`. Once this returns, the
/// caller negotiates the rest exactly like SSH mode, with one difference: setup_protocol()
/// (compat.c:602-610) skips the raw 4-byte protocol-version exchange entirely when
/// `remote_protocol` is already known from the greeting line -- see
/// <see cref="ProtocolNegotiator.Negotiate"/>'s `preNegotiatedVersion` parameter, added
/// specifically for this caller.
///
/// Auth is MD5-only, matching this v1's checksum scope throughout: real rsync negotiates an
/// auth digest from the space-separated list in the greeting line (compat.c's
/// negotiate_daemon_auth, entirely local -- no extra wire round trip), and since this
/// implementation only ever advertises "md5", a real peer resolves to md5 too (every rsync
/// version in practical use supports it). The challenge/response construction itself
/// (MD5(secret || challenge), base64 with no padding) is byte-for-byte what authenticate.c's
/// generate_hash()/base64_encode(pad=0) do, so a real rsync client's `--password-file` auth
/// against our daemon (and vice versa) is wire-compatible.
/// </summary>
public static class DaemonHandshake
{
    private const string GreetingPrefix = "@RSYNCD: ";
    private const string AuthReqdPrefix = "@RSYNCD: AUTHREQD ";

    public sealed record ServerRequest(RsyncdModule Module, bool AmSender, IReadOnlyList<string> ServerArgs, int RemoteProtocolVersion, string? AuthenticatedUser, bool Compress);

    // ---- server side ----

    /// <summary>Runs the full server-side handshake. Returns null when the connection ends
    /// without reaching a transfer (a "#list" module listing, an unknown/denied module, or a
    /// failed auth) -- the caller should just close the connection in that case, matching how a
    /// real daemon process would exit_cleanup() without ever calling start_server().</summary>
    public static ServerRequest? RunServerSide(DuplexStreamPair raw, RsyncdConfig config, IPAddress clientAddress, Action<string>? log = null)
    {
        WriteLine(raw.Output, $"{GreetingPrefix}{ProtocolNegotiator.ProtocolVersion}.0 md5");

        string? clientGreeting = ReadLine(raw.Input);
        if (clientGreeting == null || !clientGreeting.StartsWith(GreetingPrefix, StringComparison.Ordinal))
        {
            log?.Invoke($"rejected connection from {clientAddress}: no valid daemon greeting");
            return null;
        }
        int remoteVersion = ParseGreetingVersion(clientGreeting);

        string? line = ReadLine(raw.Input);
        if (line == null)
            return null;

        if (line.Length == 0 || line == "#list")
        {
            log?.Invoke($"module-list request from {clientAddress}");
            SendModuleListing(raw.Output, config, clientAddress);
            return null;
        }
        if (line[0] == '#')
        {
            WriteLine(raw.Output, $"@ERROR: Unknown command '{line}'");
            return null;
        }

        string moduleName = line.TrimEnd('/');
        var module = config.FindModule(moduleName);
        if (module == null)
        {
            log?.Invoke($"unknown module '{moduleName}' requested from {clientAddress}");
            WriteLine(raw.Output, $"@ERROR: Unknown module '{moduleName}'");
            return null;
        }

        if (!RsyncdConfig.IsHostAllowed(module, clientAddress))
        {
            log?.Invoke($"access denied to module '{moduleName}' from {clientAddress}");
            WriteLine(raw.Output, $"@ERROR: access denied to {moduleName} from {clientAddress}");
            return null;
        }

        string? authenticatedUser = null;
        if (module.RequiresAuth)
        {
            authenticatedUser = RunServerAuth(raw, module);
            if (authenticatedUser == null)
            {
                log?.Invoke($"auth failed for module '{moduleName}' from {clientAddress}");
                WriteLine(raw.Output, "@ERROR: auth failed");
                return null;
            }
        }

        // "OK" must be sent before we can know push-vs-pull -- the client only reveals that via
        // the argv list it sends AFTER seeing "OK" (real rsync's own wire layering: server_options()
        // is transmitted post-OK). So a read-only violation can't be reported within this
        // handshake without breaking wire compatibility; real rsync itself only discovers/reports
        // this once the post-handshake protocol proper is running (rsync.c's own read_only check,
        // surfaced as a normal FERROR/exit_cleanup after negotiation). This method just returns
        // AmSender/Module.ReadOnly and leaves enforcement to the caller, post-negotiation.
        WriteLine(raw.Output, "@RSYNCD: OK");

        var args = ReadServerArgs(raw.Input);
        bool amSender = args.Contains("--sender");
        bool compress = args.Any(a => HasShortFlag(a, 'z'));

        return new ServerRequest(module, amSender, args, remoteVersion, authenticatedUser, compress);
    }

    /// <summary>Detects whether the client's bundled short-option flags token (e.g.
    /// "-vlze32.0fxCIvu" -- see <see cref="Options.ServerInvocationBuilder"/>) requested
    /// <paramref name="flag"/>. This is deliberately NOT a full argv parse (RsyncArgParser's
    /// general-purpose grammar table doesn't recognize "--server"/"--sender" and would throw on
    /// them) -- just enough to answer "did the real/our client's -z make it across the wire",
    /// which is all daemon mode needs before it can know whether to participate in the
    /// compression-choice exchange (see ProtocolNegotiator.Negotiate's `compress` parameter).
    /// Scanning stops at the first 'e' in the bundle, since everything from there on is the
    /// protocol-version/capability-letter suffix (maybe_add_e_option, options.c:3192), not a
    /// real short option -- true both for our own ServerInvocationBuilder output (which always
    /// appends that suffix directly onto the same token, after any real flag letters including
    /// 'z') and for a genuine rsync client's argv, built the same way.</summary>
    private static bool HasShortFlag(string arg, char flag)
    {
        if (arg.Length < 2 || arg[0] != '-' || arg[1] == '-')
            return false;
        for (int i = 1; i < arg.Length; i++)
        {
            if (arg[i] == 'e')
                return false;
            if (arg[i] == flag)
                return true;
        }
        return false;
    }

    private static void SendModuleListing(Stream output, RsyncdConfig config, IPAddress clientAddress)
    {
        foreach (var m in config.Modules)
        {
            if (m.List && RsyncdConfig.IsHostAllowed(m, clientAddress))
                WriteLine(output, $"{m.Name,-15}\t{m.Comment}");
        }
        WriteLine(output, "@RSYNCD: EXIT");
    }

    private static string? RunServerAuth(DuplexStreamPair raw, RsyncdModule module)
    {
        if (string.IsNullOrEmpty(module.SecretsFile) || !File.Exists(module.SecretsFile))
            return null;

        string challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=');
        WriteLine(raw.Output, $"{AuthReqdPrefix}{challenge}");

        string? response = ReadLine(raw.Input);
        if (response == null)
            return null;
        int space = response.IndexOf(' ');
        if (space < 0)
            return null;
        string user = response[..space];
        string providedHash = response[(space + 1)..];

        if (!UserAllowed(module, user))
            return null;

        string? secret = LookupSecret(module.SecretsFile, user);
        if (secret == null)
            return null;

        return FixedTimeEquals(ComputeAuthHash(secret, challenge), providedHash) ? user : null;
    }

    private static bool UserAllowed(RsyncdModule module, string user) =>
        module.AuthUsers.Any(u => u == "*" || string.Equals(u, user, StringComparison.Ordinal));

    private static string? LookupSecret(string secretsFile, string user)
    {
        foreach (string raw in File.ReadLines(secretsFile))
        {
            string line = raw.TrimEnd('\r', '\n');
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            if (string.Equals(line[..colon], user, StringComparison.Ordinal))
                return line[(colon + 1)..];
        }
        return null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // ---- client side ----

    /// <summary>Runs the client-side handshake: greeting, module selection, responding to an
    /// AUTHREQD challenge if the module requires one, then sending the argv list. Used both by
    /// <c>TcpDaemonClientTransport</c> and directly by tests that want to talk to a real rsync
    /// daemon (or our own <see cref="RunServerSide"/>) without a real TCP listener.</summary>
    /// <returns>The remote daemon's protocol version, parsed from its `@RSYNCD: x.y` greeting --
    /// the caller MUST pass this to <see cref="ProtocolNegotiator.Negotiate"/>'s
    /// `preNegotiatedVersion` parameter afterward. Daemon mode negotiates the version exactly
    /// once, in this greeting line -- setup_protocol()'s raw 4-byte version exchange
    /// (compat.c:602-610) is skipped entirely once a peer already knows the version this way.
    /// Omitting this (as an earlier version of this method's caller did, since this method used
    /// to return void) left the client ALSO attempting that raw exchange after the daemon side
    /// had already moved past it, desyncing the very next read -- surfaced as a nonsense
    /// "protocol version mismatch -- remote sent &lt;garbage&gt;" the first time this project's own
    /// CLI daemon-push/pull code path was actually exercised end-to-end against its own daemon
    /// (every prior real-interop test exercised a REAL rsync daemon or REAL rsync client, never
    /// our own client against our own daemon, so this gap went unnoticed until then).</returns>
    public static int RunClientSide(DuplexStreamPair raw, string moduleName, IReadOnlyList<string> serverArgs, string? username = null, string? password = null)
    {
        WriteLine(raw.Output, $"{GreetingPrefix}{ProtocolNegotiator.ProtocolVersion}.0 md5");

        string serverGreeting = ReadLine(raw.Input) ?? throw new InvalidDataException("no daemon greeting from server");
        if (!serverGreeting.StartsWith(GreetingPrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"not a daemon greeting: {serverGreeting}");
        int remoteVersion = ParseGreetingVersion(serverGreeting);

        WriteLine(raw.Output, moduleName);

        while (true)
        {
            string line = ReadLine(raw.Input) ?? throw new InvalidDataException("server closed connection during module handshake");
            if (line.StartsWith(AuthReqdPrefix, StringComparison.Ordinal))
            {
                string challenge = line[AuthReqdPrefix.Length..];
                string user = string.IsNullOrEmpty(username) ? "nobody" : username;
                string hash = ComputeAuthHash(password ?? "", challenge);
                WriteLine(raw.Output, $"{user} {hash}");
                continue;
            }
            if (line == "@RSYNCD: OK")
                break;
            if (line == "@RSYNCD: EXIT")
                throw new InvalidOperationException("daemon ended the connection (module-listing mode)");
            if (line.StartsWith("@ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException(line);
            // Otherwise a MOTD/informational line -- ignored in this v1 (no --no-motd suppression yet).
        }

        foreach (string arg in serverArgs)
            WriteCString(raw.Output, arg);
        raw.Output.WriteByte(0); // final empty string terminates the arg list (clientserver.c:446)
        raw.Output.Flush();

        return remoteVersion;
    }

    /// <summary>Reads the module listing a "#list" request (or a bare empty line) returns,
    /// stopping at "@RSYNCD: EXIT" or EOF.</summary>
    public static IReadOnlyList<string> RunClientListModules(DuplexStreamPair raw)
    {
        WriteLine(raw.Output, $"{GreetingPrefix}{ProtocolNegotiator.ProtocolVersion}.0 md5");
        _ = ReadLine(raw.Input) ?? throw new InvalidDataException("no daemon greeting from server");
        WriteLine(raw.Output, "#list");

        var lines = new List<string>();
        while (true)
        {
            string? line = ReadLine(raw.Input);
            if (line == null || line == "@RSYNCD: EXIT")
                break;
            lines.Add(line);
        }
        return lines;
    }

    // ---- shared auth math ----

    /// <summary>generate_hash() (authenticate.c:123): base64(MD5(secret || challenge)), no
    /// padding. Order matters -- secret bytes first, then the challenge string's bytes.</summary>
    private static string ComputeAuthHash(string secret, string challenge)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] challengeBytes = Encoding.UTF8.GetBytes(challenge);
        using var md5 = MD5.Create();
        md5.TransformBlock(secretBytes, 0, secretBytes.Length, null, 0);
        md5.TransformFinalBlock(challengeBytes, 0, challengeBytes.Length);
        return Convert.ToBase64String(md5.Hash!).TrimEnd('=');
    }

    private static int ParseGreetingVersion(string greeting)
    {
        // "@RSYNCD: 32.0 md5" -- version is the integer before the '.'.
        string rest = greeting[GreetingPrefix.Length..];
        int dot = rest.IndexOf('.');
        int space = rest.IndexOf(' ');
        int end = dot >= 0 ? dot : (space >= 0 ? space : rest.Length);
        return int.TryParse(rest[..end], out int v) ? v : ProtocolNegotiator.ProtocolVersion;
    }

    // ---- line/arg wire primitives (text preamble, not multiplexed) ----

    private static void WriteLine(Stream s, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        s.Write(bytes);
        s.Flush();
    }

    /// <summary>read_line_old (io.c): reads up to '\n', strips it and any trailing '\r'.
    /// Returns null on immediate EOF (no bytes read at all).</summary>
    private static string? ReadLine(Stream s, int maxLen = 8192)
    {
        var bytes = new List<byte>();
        int read = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0)
                return read == 0 ? null : DecodeLine(bytes);
            read++;
            if (b == '\n')
                return DecodeLine(bytes);
            bytes.Add((byte)b);
            if (bytes.Count > maxLen)
                throw new InvalidDataException("daemon handshake line too long");
        }
    }

    private static string DecodeLine(List<byte> bytes)
    {
        int len = bytes.Count;
        if (len > 0 && bytes[len - 1] == '\r')
            len--;
        return Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..len]);
    }

    private static void WriteCString(Stream s, string text)
    {
        s.Write(Encoding.UTF8.GetBytes(text));
        s.WriteByte(0);
    }

    /// <summary>Reads NUL-terminated strings until an empty one terminates the list
    /// (clientserver.c:440-446's rl_nulls form -- always true for us, protocol &gt;= 30).</summary>
    private static List<string> ReadServerArgs(Stream s)
    {
        var args = new List<string>();
        while (true)
        {
            string arg = ReadCString(s);
            if (arg.Length == 0)
                return args;
            args.Add(arg);
        }
    }

    private static string ReadCString(Stream s)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("daemon handshake ended mid arg list");
            if (b == 0)
                return Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes));
            bytes.Add((byte)b);
        }
    }
}
