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

namespace RsyncWindows.Core.Checksums;

/// <summary>
/// Ports get_checksum1() (checksum.c:303) -- rsync's Adler32-inspired weak/rolling checksum.
/// CHAR_OFFSET is 0 in this rsync version (rsync.h:43; historically nonzero, kept for
/// documentation), so the widely-known "unrolled by 4" C loop is mathematically identical to
/// a plain per-byte accumulation (verified by hand: expanding the unrolled formula over 4
/// bytes reduces to the same s1/s2 as four sequential per-byte steps) -- this port uses the
/// simple per-byte form for clarity, not the unrolling, since they produce bit-identical results.
///
/// Bytes are read as SIGNED (schar in the C source) before accumulating -- this matters for
/// wire-compatibility with a real peer and is easy to get wrong by accident in C# where byte
/// is unsigned by default.
/// </summary>
public static class RollingChecksum
{
    /// <summary>Full computation over a window, returning the (s1, s2) halves the way
    /// hash_search() extracts them from get_checksum1()'s packed return value (each masked
    /// to 16 bits, per get_checksum1's own `(s1 &amp; 0xffff) + (s2 &lt;&lt; 16)` packing).</summary>
    public static (uint S1, uint S2) Compute(ReadOnlySpan<byte> data)
    {
        uint s1 = 0, s2 = 0;
        foreach (byte b in data)
        {
            s1 += unchecked((uint)(sbyte)b);
            s2 += s1;
        }
        return (s1 & 0xFFFF, s2 & 0xFFFF);
    }

    /// <summary>Packs (s1, s2) into the combined 32-bit value used for hash-table bucketing
    /// and stored-checksum comparison (match.c: `sum = (s1 &amp; 0xffff) | (s2 &lt;&lt; 16)`).</summary>
    public static uint Pack(uint s1, uint s2) => (s1 & 0xFFFF) | (s2 << 16);

    /// <summary>
    /// One step of the incremental rolling update from hash_search() (match.c:356-364):
    /// removes the byte leaving the front of the window and, if there's a next byte to slide
    /// in, adds it; otherwise the window has hit true EOF and shrinks by one (signaled by
    /// returning <paramref name="windowLength"/> - 1 via the out parameter). s1/s2 are
    /// intentionally NOT re-masked to 16 bits here, matching the C code -- every point that
    /// actually USES s1/s2 (via <see cref="Pack"/>) re-applies the mask, and unsigned
    /// wraparound during the intermediate arithmetic is exactly what the original does too.
    /// </summary>
    public static void Roll(ref uint s1, ref uint s2, ref int windowLength, byte outgoing, byte? incoming)
    {
        uint outgoingVal = unchecked((uint)(sbyte)outgoing);
        s1 -= outgoingVal;
        s2 -= unchecked((uint)windowLength) * outgoingVal;

        if (incoming.HasValue)
        {
            s1 += unchecked((uint)(sbyte)incoming.Value);
            s2 += s1;
        }
        else
        {
            windowLength--;
        }
    }
}
