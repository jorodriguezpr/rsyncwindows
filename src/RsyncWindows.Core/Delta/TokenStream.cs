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

using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.Delta;

/// <summary>
/// Ports the uncompressed token framing (simple_send_token/simple_recv_token, token.c:289/317).
/// Compression (-z's raw-deflate framing on top of this) is Phase 6 -- deferred, matching the
/// plan's phasing.
///
/// Wire shape: a literal run is one or more (length, bytes) pairs -- chunked at
/// <see cref="ChunkSize"/> bytes per write_int-prefixed piece, since real rsync's receiver-side
/// buffer is sized to that chunk regardless of how large the logical run is -- followed by a
/// single token integer: 0 marks end-of-stream, a negative value -(blockIndex+1) references a
/// matched basis-file block. A pure end-of-stream marker with no literal bytes is just the
/// integer 0 on its own (n=0 skips the literal-run loop entirely).
/// </summary>
public static class TokenStream
{
    private const int ChunkSize = 32 * 1024; // token.c CHUNK_SIZE

    public static void WriteLiteralAndToken(Stream s, ReadOnlySpan<byte> literal, int blockIndex)
    {
        WriteLiteralChunks(s, literal);
        WireCodec.WriteInt32(s, -(blockIndex + 1));
    }

    /// <summary>Writes the final end-of-stream marker (a literal run, if any pending bytes,
    /// followed by the token value 0).</summary>
    public static void WriteFinalLiteralAndEnd(Stream s, ReadOnlySpan<byte> literal)
    {
        WriteLiteralChunks(s, literal);
        WireCodec.WriteInt32(s, 0);
    }

    private static void WriteLiteralChunks(Stream s, ReadOnlySpan<byte> literal)
    {
        int written = 0;
        while (written < literal.Length)
        {
            int n = Math.Min(ChunkSize, literal.Length - written);
            WireCodec.WriteInt32(s, n);
            s.Write(literal.Slice(written, n));
            written += n;
        }
    }

    /// <summary>One decoded step of the token stream: either a literal byte run, a matched
    /// block-index reference, or end-of-stream.</summary>
    public readonly struct Token
    {
        public byte[]? Literal { get; init; }
        public int? BlockIndex { get; init; }
        public bool IsEnd { get; init; }
    }

    /// <summary>Reads one token-stream "event". A literal run longer than
    /// <see cref="ChunkSize"/> is delivered across multiple calls (matching simple_recv_token's
    /// own residue-chunking) -- callers should keep calling until <see cref="Token.IsEnd"/>.</summary>
    public static Token Read(Stream s)
    {
        int i = WireCodec.ReadInt32(s);
        if (i <= 0)
            return i == 0 ? new Token { IsEnd = true } : new Token { BlockIndex = -i - 1 };

        var buf = new byte[i];
        s.ReadExactly(buf);
        return new Token { Literal = buf };
    }
}
