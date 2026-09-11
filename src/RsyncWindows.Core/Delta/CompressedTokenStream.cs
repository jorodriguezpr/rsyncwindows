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

using RsyncWindows.Core.Delta.Zlib;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.Delta;

/// <summary>
/// Ports the `-z` compressed token framing (send_deflated_token/recv_deflated_token, token.c)
/// on top of raw (no zlib header/trailer) DEFLATE, matching real rsync's own
/// `deflateInit2(..., -15, ...)`/`inflateInit2(..., -15)`. Uses <see cref="RawDeflater"/>/
/// <see cref="RawInflater"/> -- a small P/Invoke wrapper around real zlib via
/// `System.IO.Compression.Native` (see that class's doc comment) -- rather than .NET's public
/// `DeflateStream` API (no manual sync-flush control) or SharpZipLib's managed `Deflater`
/// (CONFIRMED, via a full root-cause investigation this session, to corrupt any flush sequence
/// where an earlier flush in the same stream happened to pick a raw STORED block -- its
/// `Flush()` completion logic unconditionally appends Huffman-continuation padding regardless of
/// the preceding block's actual type; genuine zlib's `Z_SYNC_FLUSH` does not have this bug, which
/// is exactly why real rsync's own C source can rely on this same discard-and-flush priming
/// technique against a real system zlib).
///
/// Wire shape (flag byte in the low 6/8 bits, matching token.c's `#define`s exactly):
/// `END_FLAG`(0x00) / `TOKEN_LONG`(0x20)+abs-index / `TOKENRUN_LONG`(0x21)+abs-index+2-byte-count /
/// `DEFLATED_DATA`(0x40)+14-bit-length+bytes / `TOKEN_REL`(0x80)+6-bit-delta /
/// `TOKENRUN_REL`(0xC0)+6-bit-delta+2-byte-count. Consecutive matched-block references are
/// run-length encoded (a "run" of block indices with no literal data between them); each
/// literal run is DEFLATE-compressed, sync-flushed, and trimmed of the trailing 4-byte
/// `00 00 FF FF` sync marker before framing (the receiver re-appends it before inflating) --
/// matching real rsync's actual raw-zlib convention exactly, now that a genuine zlib backs this
/// class (an earlier SharpZipLib-based revision found trimming corrupted its output; that was a
/// symptom of SharpZipLib's own Flush() not producing the standard marker at all, not a property
/// of the technique itself).
///
/// Deliberately NOT ported: `Z_INSERT_ONLY` specifically (token.c:475-511's primary path,
/// requiring a patched zlib most systems don't have) -- this uses that function's OWN documented
/// fallback instead (plain `Z_SYNC_FLUSH` and discard the output), which real rsync's own source
/// comment describes as producing valid, standards-compliant DEFLATE data with a real zlib,
/// just a slightly worse compression ratio than `Z_INSERT_ONLY` would give on some inputs.
/// </summary>
public static class CompressedTokenStream
{
    internal const int MaxDataCount = 16383; // MAX_DATA_COUNT (token.c) -- 14-bit length field
    private const byte EndFlag = 0x00;
    private const byte TokenLong = 0x20;
    private const byte TokenRunLong = 0x21;
    private const byte DeflatedData = 0x40;
    private const byte TokenRel = 0x80;
    private const byte TokenRunRel = 0xC0;

    /// <summary>The fixed trailing bytes a raw-zlib `Z_SYNC_FLUSH` always produces: an empty
    /// stored block (`BFINAL=0,BTYPE=00`, `LEN=0x0000`, `NLEN=0xFFFF`) used purely to force byte
    /// alignment. Real rsync's sender trims exactly these 4 bytes before sending; the receiver
    /// re-appends them before inflating (recv_deflated_token's "the decompressor should be
    /// expecting to see the 0,0,ff,ff bytes" step, token.c).</summary>
    internal static readonly byte[] SyncFlushMarker = [0x00, 0x00, 0xFF, 0xFF];
}

/// <summary>Write side -- one instance per file (matching send_deflated_token's own per-file
/// deflateReset, triggered there by `last_token` returning to -1 after each file's END_FLAG;
/// since a fresh instance already starts in that state, this class is simply never reused
/// across files rather than needing an explicit reset method).</summary>
public sealed class CompressedTokenWriter : IDisposable
{
    private readonly RawDeflater _deflater = new();
    private int _lastToken = -1; // -1 = fresh, no run started yet
    private int _runStart;
    private int _lastRunEnd;

    public void WriteLiteralAndToken(Stream s, ReadOnlySpan<byte> literal, int blockIndex)
    {
        AdvanceRun(s, blockIndex, literal.Length > 0);
        _lastToken = blockIndex;
        if (literal.Length > 0)
            WriteCompressedLiteral(s, literal);
    }

    public void WriteFinalLiteralAndEnd(Stream s, ReadOnlySpan<byte> literal)
    {
        if (_lastToken != -1)
            EmitRun(s); // token=-1 (end) can never equal _lastToken+1, so a pending run always flushes
        if (literal.Length > 0)
            WriteCompressedLiteral(s, literal);
        s.WriteByte(0x00); // END_FLAG
    }

    private void AdvanceRun(Stream s, int token, bool hasLiteral)
    {
        if (_lastToken == -1)
        {
            _lastRunEnd = 0;
            _runStart = token;
            return;
        }
        if (hasLiteral || token != _lastToken + 1 || token >= _runStart + 65536)
        {
            EmitRun(s);
            _runStart = token;
        }
    }

    private void EmitRun(Stream s)
    {
        int r = _runStart - _lastRunEnd;
        int n = _lastToken - _runStart;
        if (r is >= 0 and <= 63)
        {
            s.WriteByte((byte)((n == 0 ? 0x80 : 0xC0) + r));
        }
        else
        {
            s.WriteByte((byte)(n == 0 ? 0x20 : 0x21));
            WireCodec.WriteInt32(s, _runStart);
        }
        if (n != 0)
        {
            s.WriteByte((byte)n);
            s.WriteByte((byte)(n >> 8));
        }
        _lastRunEnd = _lastToken;
    }

    /// <summary>Ports the fallback branch of send_deflated_token's own history-priming
    /// (token.c:475-511, the "system zlib lacking Z_INSERT_ONLY" path): feeds a matched block's
    /// bytes through the SAME deflater instance used for real literal output, sync-flushing and
    /// discarding whatever it produces. NOT an optional optimization here: without this, this
    /// writer's DEFLATE window diverges from <see cref="CompressedTokenReader"/>'s (which
    /// unconditionally primes from every matched block via `PrimeFromMatchedBlock`) the moment
    /// a matched block sits between two literal runs -- any back-reference the later literal's
    /// compression could have made into the earlier literal would then decode against the wrong
    /// window offset, since the reader's window has extra (primed) bytes the writer's window
    /// never gained. Confirmed directly via a round-trip unit test regression before this was
    /// added: decoding a literal run positioned after a primed block silently produced bytes
    /// from the primed block's own content instead of the real literal.</summary>
    public void PrimeFromMatchedBlock(ReadOnlySpan<byte> blockData)
    {
        if (blockData.Length == 0)
            return;
        _deflater.SetInput(blockData.ToArray());
        var outBuf = new byte[8192];
        DrainDeflate(outBuf, discard: true, sink: null);
        _deflater.Flush();
        DrainDeflate(outBuf, discard: true, sink: null);
    }

    /// <summary>See <see cref="CompressedTokenReader.DrainInflate"/>'s doc comment for why a
    /// naive `while (!IsNeedingInput)` loop is not a safe "fully drained" signal on its own for
    /// this API -- the same caution applies symmetrically to the deflater.</summary>
    private void DrainDeflate(byte[] outBuf, bool discard, MemoryStream? sink)
    {
        while (true)
        {
            int n = _deflater.Deflate(outBuf);
            if (n > 0)
            {
                if (!discard)
                    sink!.Write(outBuf, 0, n);
                continue;
            }
            if (_deflater.IsNeedingInput)
                break;
            break;
        }
    }

    /// <summary>Deflates one literal run: feed all bytes, sync-flush-drain, trim the trailing
    /// 4-byte `00 00 FF FF` marker <see cref="CompressedTokenStream.SyncFlushMarker"/> a real
    /// zlib `Z_SYNC_FLUSH` always ends with (the receiver re-appends it), then frame into one or
    /// more DEFLATED_DATA chunks of at most <see cref="CompressedTokenStream.MaxDataCount"/>
    /// bytes.</summary>
    private void WriteCompressedLiteral(Stream s, ReadOnlySpan<byte> literal)
    {
        using var buffered = new MemoryStream();
        byte[] input = literal.ToArray();
        _deflater.SetInput(input);
        var outBuf = new byte[8192];
        DrainDeflate(outBuf, discard: false, sink: buffered);
        _deflater.Flush();
        DrainDeflate(outBuf, discard: false, sink: buffered);

        byte[] compressed = buffered.ToArray();
        var marker = CompressedTokenStream.SyncFlushMarker;
        if (compressed.Length >= marker.Length && compressed.AsSpan(^marker.Length).SequenceEqual(marker))
            compressed = compressed[..^marker.Length];

        int offset = 0;
        while (offset < compressed.Length)
        {
            int n = Math.Min(CompressedTokenStream.MaxDataCount, compressed.Length - offset);
            s.WriteByte((byte)(0x40 + (n >> 8)));
            s.WriteByte((byte)n);
            s.Write(compressed, offset, n);
            offset += n;
        }
    }

    public void Dispose() => _deflater.Dispose();
}

/// <summary>Read side -- one instance per file, mirroring <see cref="CompressedTokenWriter"/>'s
/// per-file lifetime.</summary>
public sealed class CompressedTokenReader : IDisposable
{
    private readonly RawInflater _inflater = new();
    private bool _runActive;
    private int _rxToken;
    private int _rxRun;
    private bool _literalRunOpen; // true once we've seen at least one DEFLATED_DATA chunk since the last non-data flag

    public TokenStream.Token Read(Stream s)
    {
        if (_runActive)
        {
            _rxToken++;
            if (--_rxRun == 0)
                _runActive = false;
            return new TokenStream.Token { BlockIndex = _rxToken };
        }

        while (true)
        {
            int flag = ReadByte(s);
            if ((flag & 0xC0) == 0x40) // DEFLATED_DATA
            {
                int n = ((flag & 0x3F) << 8) | ReadByte(s);
                var chunk = new byte[n];
                s.ReadExactly(chunk);
                byte[] produced = InflateChunk(chunk);
                _literalRunOpen = true;
                if (produced.Length > 0)
                    return new TokenStream.Token { Literal = produced };
                continue; // an empty inflate result (rare, but possible) -- keep reading
            }

            if (_literalRunOpen)
            {
                FinishLiteralRun();
                _literalRunOpen = false;
            }

            if (flag == 0x00) // END_FLAG
                return new TokenStream.Token { IsEnd = true };

            return DecodeTokenFlag(s, flag);
        }
    }

    /// <summary>Ports see_deflate_token() (token.c:688-727): feeds a matched basis block's bytes
    /// into the inflater's history via a hand-built raw-DEFLATE "stored block" (BFINAL=0,
    /// BTYPE=00, then a little-endian LEN/~LEN header and the literal bytes) -- entirely
    /// standard DEFLATE, no zlib extension needed. Must be called by the caller (DeltaReceiver)
    /// immediately after this reader yields a matched-block token, passing that block's actual
    /// bytes from the basis file, so this reader's inflate window stays in sync with whatever a
    /// real sender's compressor may have referenced from that same matched region.</summary>
    public void PrimeFromMatchedBlock(ReadOnlySpan<byte> blockData)
    {
        int offset = 0;
        int remaining = blockData.Length;
        var outBuf = new byte[8192];
        do
        {
            int blockLen = Math.Min(remaining, 0xFFFF);
            byte[] stored = new byte[5 + blockLen];
            stored[0] = 0; // BFINAL=0, BTYPE=00 (stored), bit-aligned since inflate is byte-aligned between blocks here
            stored[1] = (byte)blockLen;
            stored[2] = (byte)(blockLen >> 8);
            stored[3] = (byte)~stored[1];
            stored[4] = (byte)~stored[2];
            blockData.Slice(offset, blockLen).CopyTo(stored.AsSpan(5));
            offset += blockLen;
            remaining -= blockLen;

            _inflater.SetInput(stored);
            DrainInflate(outBuf, discard: true);
        } while (remaining > 0);
    }

    /// <summary>Keeps calling Inflate() until a call both produces zero bytes AND the inflater
    /// reports it needs more input -- <c>IsNeedingInput</c> alone is not a reliable "fully
    /// drained" signal on its own: it can flip true after the input bytes are consumed
    /// internally while output is still pending extraction, so a naive `while
    /// (!IsNeedingInput)` loop can exit one call early and leave bytes to leak into a later,
    /// unrelated Inflate() call (observed directly against SharpZipLib's Inflater: a
    /// matched-block priming call's own bytes bleeding into the next real literal decode's
    /// output; kept as a defensive guard here too even though this project's own
    /// <see cref="RawInflater"/> always fully drains a single native call's output before
    /// returning, since the contract this drains against is the same either way).</summary>
    private void DrainInflate(byte[] outBuf, bool discard, MemoryStream? sink = null)
    {
        while (true)
        {
            int n = _inflater.Inflate(outBuf);
            if (n > 0)
            {
                if (!discard)
                    sink!.Write(outBuf, 0, n);
                continue;
            }
            if (_inflater.IsNeedingInput)
                break;
            break; // n == 0 but still not needing input -- nothing more to safely extract right now
        }
    }

    private byte[] InflateChunk(byte[] compressed)
    {
        _inflater.SetInput(compressed);
        using var output = new MemoryStream();
        var outBuf = new byte[8192];
        DrainInflate(outBuf, discard: false, sink: output);
        return output.ToArray();
    }

    /// <summary>Feeds the trimmed 4-byte sync marker back in (matching what the writer trimmed),
    /// draining any final pending output -- the counterpart to recv_deflated_token's
    /// "at this point the decompressor should be expecting to see the 0,0,ff,ff bytes" step.
    /// Called once a DEFLATED_DATA run ends (the next flag read is something other than another
    /// DEFLATED_DATA chunk), matching how real rsync only needs this once per literal run, not
    /// once per wire chunk within it.</summary>
    private void FinishLiteralRun()
    {
        var outBuf = new byte[8192];
        _inflater.SetInput(CompressedTokenStream.SyncFlushMarker);
        DrainInflate(outBuf, discard: true);
    }

    private TokenStream.Token DecodeTokenFlag(Stream s, int flag)
    {
        bool runFollows;
        if ((flag & TokenRelBit) != 0)
        {
            int incr = flag & 0x3F;
            _rxToken += incr;
            runFollows = ((flag >> 6) & 1) != 0;
        }
        else
        {
            _rxToken = WireCodec.ReadInt32(s);
            runFollows = (flag & 1) != 0;
        }

        if (runFollows)
        {
            int lo = ReadByte(s);
            int hi = ReadByte(s);
            _rxRun = lo + (hi << 8);
            _runActive = true;
        }

        return new TokenStream.Token { BlockIndex = _rxToken };
    }

    private const int TokenRelBit = 0x80;

    private static int ReadByte(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0)
            throw new EndOfStreamException();
        return b;
    }

    public void Dispose() => _inflater.Dispose();
}
