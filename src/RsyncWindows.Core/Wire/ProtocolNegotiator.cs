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

namespace RsyncWindows.Core.Wire;

/// <summary>Result of a completed handshake: the negotiated parameters plus the connection,
/// now wrapped in multiplexed streams (multiplexing starts immediately after negotiation for
/// every protocol version we support — see main.c's io_start_multiplex_in/out call sites).</summary>
public sealed record NegotiatedSession(
    int ProtocolVersion,
    CompatFlags CompatFlags,
    int ChecksumSeed,
    bool ProperSeedOrder,
    MultiplexReadStream Input,
    MultiplexWriteStream Output,
    bool CompressionEnabled = false);

/// <summary>
/// Ports rsync's setup_protocol() (compat.c:576) plus the checksum/compression algorithm-name
/// negotiation (negotiate_the_strings, compat.c:538). This targets our own protocol-32-only,
/// MD5-only, no-compression v1 — real interop against an actual rsync peer (which announces a
/// richer client_info capability string and a longer checksum/compression name list) is a
/// Phase 4 concern once RsyncArgParser exists to derive that string from real argv.
///
/// Call sequence (both sides call this once the raw transport is connected — no daemon
/// greeting/module/auth here; that's TcpDaemonClient/ServerTransport's job before handing off
/// to this method): raw 4-byte protocol-version exchange (symmetric) -> compat-flags varint
/// (only the "server" role computes and announces them, compat.c:722-755) -> checksum-name
/// vstring exchange (symmetric) -> 4-byte checksum seed (server generates, client reads,
/// compat.c:822-828) -> multiplexing turns on.
/// </summary>
public static class ProtocolNegotiator
{
    public const int ProtocolVersion = 32;
    public const int MinProtocolVersion = 20;
    public const int MaxProtocolVersion = 40;

    /// <param name="raw">The raw (pre-multiplex) transport streams.</param>
    /// <param name="isServer">True for the "am_server" role (the remote-invoked side over SSH,
    /// or the daemon process in TCP-daemon mode) — NOT the same distinction as sender/receiver.</param>
    /// <param name="onMessage">Invoked for any non-MSG_DATA frame received after multiplexing
    /// starts (MSG_INFO/WARNING/ERROR/STATS/...).</param>
    /// <param name="preNegotiatedVersion">Pass the remote protocol version already learned from
    /// a daemon-mode text greeting (<see cref="Daemon.DaemonHandshake"/>) to skip the raw 4-byte
    /// exchange entirely, matching setup_protocol()'s `if (remote_protocol == 0)` guard
    /// (compat.c:602-610) -- daemon mode negotiates the version once, in the `@RSYNCD: x.y`
    /// greeting line, not twice.</param>
    /// <param name="compress">This side's own `do_compression` (i.e. whether `-z` was requested)
    /// -- derived independently by each caller from its own parsed argv, matching real rsync's
    /// negotiate_the_strings() (compat.c:538-574), which only performs the compression-choice
    /// exchange `if (do_compression && !compress_choice)`. Both sides must arrive at the same
    /// value for this to stay in sync: real rsync guarantees that by transmitting the SAME
    /// parsed `-z` flag to the remote as part of its own server-invocation argv (see
    /// <see cref="Options.ServerInvocationBuilder"/>), so both sides parse it from the identical
    /// source of truth rather than negotiating it here.</param>
    public static NegotiatedSession Negotiate(DuplexStreamPair raw, bool isServer, Action<MsgCode, byte[]>? onMessage = null, int? preNegotiatedVersion = null, bool compress = false)
    {
        int remoteVersion;
        if (preNegotiatedVersion is { } known)
        {
            remoteVersion = known;
        }
        else
        {
            WireCodec.WriteInt32(raw.Output, ProtocolVersion);
            remoteVersion = WireCodec.ReadInt32(raw.Input);
        }
        if (remoteVersion < MinProtocolVersion || remoteVersion > MaxProtocolVersion)
            throw new InvalidDataException($"protocol version mismatch -- remote sent {remoteVersion}");
        int negotiated = Math.Min(ProtocolVersion, remoteVersion);

        CompatFlags flags = CompatFlags.None;
        if (negotiated >= 30)
        {
            if (isServer)
            {
                // Modern, fixed capability set for our own v1 (no incremental recursion yet;
                // "safe" flist end marker; corrected seed order; varint-encoded flist flags).
                flags = CompatFlags.SafeFlist | CompatFlags.ChecksumSeedFix | CompatFlags.VarintFlistFlags;
                WireCodec.WriteVarInt(raw.Output, (int)flags);
            }
            else
            {
                // read_varint() is deliberately compatible with an older peer's plain write_byte()
                // when the 0x80 bit isn't set (compat.c:751) -- ReadVarInt handles both forms.
                flags = (CompatFlags)WireCodec.ReadVarInt(raw.Input);
            }
        }
        bool properSeedOrder = flags.HasFlag(CompatFlags.ChecksumSeedFix);

        const string ourChecksumChoices = "md5"; // the only strong-checksum algorithm this v1 implements
        WireCodec.WriteVString(raw.Output, Encoding.ASCII.GetBytes(ourChecksumChoices));
        string peerChecksumChoices = Encoding.ASCII.GetString(WireCodec.ReadVString(raw.Input));
        if (!peerChecksumChoices.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("md5"))
        {
            throw new InvalidDataException(
                $"peer's checksum-choice list ('{peerChecksumChoices}') does not include md5, " +
                "the only algorithm this implementation offers");
        }
        bool compressionEnabled = false;
        if (compress)
        {
            // ourCompressChoices deliberately offers only "zlib" -- the classic, per-file-reset
            // token.c scheme CompressedTokenStream ports (send_deflated_token/recv_deflated_token)
            // -- NOT "zlibx" (the newer full-flush variant some modern rsync builds prefer by
            // default). A real peer's list always still contains "zlib" for back-compat, so this
            // is enough to guarantee a match without implementing the general multi-choice
            // preference algorithm real negotiate_the_strings() uses.
            const string ourCompressChoices = "zlib";
            WireCodec.WriteVString(raw.Output, Encoding.ASCII.GetBytes(ourCompressChoices));
            string peerCompressChoices = Encoding.ASCII.GetString(WireCodec.ReadVString(raw.Input));
            if (!peerCompressChoices.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("zlib"))
            {
                throw new InvalidDataException(
                    $"peer's compression-choice list ('{peerCompressChoices}') does not include zlib, " +
                    "the only algorithm this implementation offers");
            }
            compressionEnabled = true;
        }

        int checksumSeed;
        if (isServer)
        {
            checksumSeed = Random.Shared.Next();
            WireCodec.WriteInt32(raw.Output, checksumSeed);
        }
        else
        {
            checksumSeed = WireCodec.ReadInt32(raw.Input);
        }

        var multiplexedIn = new MultiplexReadStream(raw.Input, onMessage);
        var multiplexedOut = new MultiplexWriteStream(raw.Output);

        return new NegotiatedSession(negotiated, flags, checksumSeed, properSeedOrder, multiplexedIn, multiplexedOut, compressionEnabled);
    }
}
