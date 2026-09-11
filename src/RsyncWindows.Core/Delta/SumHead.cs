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

/// <summary>Ports struct sum_struct's header fields and write_sum_head()/read_sum_head()
/// (io.c:2257/2195) -- always the protocol>=27 wire shape (4 fixed 4-byte ints; we never talk
/// to anything older than protocol 30).</summary>
public sealed record SumHead(int Count, int BlockLength, int S2Length, int Remainder)
{
    public void Write(Stream s)
    {
        WireCodec.WriteInt32(s, Count);
        WireCodec.WriteInt32(s, BlockLength);
        WireCodec.WriteInt32(s, S2Length);
        WireCodec.WriteInt32(s, Remainder);
    }

    public static SumHead Read(Stream s)
    {
        int count = WireCodec.ReadInt32(s);
        int blength = WireCodec.ReadInt32(s);
        int s2length = WireCodec.ReadInt32(s);
        int remainder = WireCodec.ReadInt32(s);
        return new SumHead(count, blength, s2length, remainder);
    }

    /// <summary>Builds the header for a basis file of the given length, per
    /// sum_sizes_sqroot() (generator.c:703): block length from
    /// <see cref="Checksums.BlockSizer"/>, and s2length computed the SAME way the real
    /// generator computes it for a file of this length.
    ///
    /// Real rsync's initial per-file checksum length is SHORT_SUM_LENGTH = 2 (io.c:82,
    /// `int csum_length = SHORT_SUM_LENGTH`), NOT the full digest: it only escalates to
    /// SUM_LENGTH (16) per file after a whole-file digest mismatch triggers the redo
    /// queue. sum_sizes_sqroot() (generator.c:732-751) then derives the actual per-file
    /// s2length from that starting length via the BLOCKSUM_BIAS heuristic, clamped to
    /// [csum_length, MIN(SUM_LENGTH, xfer_sum_len)]. We advertise "md5" as our checksum
    /// algorithm, so xfer_sum_len = MD5 digest length = 16 = SUM_LENGTH, giving
    /// max_s2length = 16.
    ///
    /// An earlier version of this method hard-coded the full 16-byte digest here. That
    /// broke BOTH directions against a real peer (each surface as a protocol desync rather
    /// than a clean checksum failure):
    ///
    /// - As the GENERATOR (real sender pulls our checksum table -- Phase 5 daemon PULL):
    ///   the real sender's receive_sums() reads head.s2length and then exactly that many
    ///   bytes per block. Our 16 forced a 14-byte over-read per block, misaligning
    ///   everything after the first block; the real sender's subsequent NDX_DONE replies
    ///   were then misread by our generator bookkeeping, and the exchange died in the
    ///   receiver's phase counter ("got transfer request in phase 2").
    ///
    ///   (Also note the real sender VALIDATES s2length: `if (sum->s2length < 0 ||
    ///   sum->s2length > xfer_sum_len) exit_cleanup(RERR_PROTOCOL)` -- s2length=16 with
    ///   md5 (xfer_sum_len=16) passes that check, so the failure is silent framing drift,
    ///   not a clean rejection.)
    ///
    ///   (Also note: a real sender only ever compares the first s2length bytes of any
    ///   strong checksum it computes -- match.c strong_sum comparison reads exactly
    ///   head.s2length bytes -- so a LONGER checksum would "work" for matching but still
    ///   poisons the stream framing above.)
    ///
    /// - As the SENDER receiving a real generator's table: the real generator computes
    ///   its OWN s2length via this same formula (typically 2), and puts exactly that many
    ///   bytes per block on the wire. Our reader must consume exactly head.S2Length bytes
    ///   per block -- which it does, but ONLY if the head it reads was built by a real
    ///   peer; our OWN generated tables must therefore also match the real formula so our
    ///   own sessions behave identically.
    ///
    /// Matching sum_sizes_sqroot() exactly (BLOCKSUM_BIAS = 32, rsync.h) makes the value
    /// we write in a sum-head we originate equal what a real peer computes for the same
    /// file length, and what we read from a real peer's head agree with the per-block
    /// strong-checksum widths it actually put on the wire.
    /// </summary>
    public static SumHead ForFileLength(long length)
    {
        int blength = Checksums.BlockSizer.ComputeBlockLength(length);
        int remainder = (int)(length % blength);
        int count = (int)(length / blength) + (remainder != 0 ? 1 : 0);

        // Real rsync starts every file at csum_length = SHORT_SUM_LENGTH (see the class
        // doc comment), so the csum_length == SUM_LENGTH branch of sum_sizes_sqroot()
        // (generator.c:726-727) is NOT the path real peers exercise in practice -- the
        // BLOCKSUM_BIAS reduction below is. Port it verbatim; it yields small s2lengths
        // (2 for typical file sizes, growing only for very large files).
        const int csumLength = Checksums.StrongChecksum.ShortSumLength;
        int maxS2Length = Math.Min(Checksums.StrongChecksum.Length, Checksums.StrongChecksum.Length);
        int s2length;
        if (csumLength == Checksums.StrongChecksum.Length)
        {
            s2length = maxS2Length;
        }
        else
        {
            const int BlocksumBias = 32; // BLOCKSUM_BIAS (rsync.h) -- from 2**32 max block count
            int b = BlocksumBias;
            for (long l = length; l > 1; l >>= 1)
                b += 2;
            int c = blength;
            while ((c >>= 1) != 0 && b != 0)
                b--;
            // add a bit, subtract rollsum, round up (generator.c:750)
            s2length = (b + 1 - 32 + 7) / 8;
            s2length = Math.Max(s2length, csumLength);
            s2length = Math.Min(s2length, maxS2Length);
        }

        return new SumHead(count, blength, s2length, remainder);
    }
}
