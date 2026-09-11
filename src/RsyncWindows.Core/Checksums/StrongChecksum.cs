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

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RsyncWindows.Core.Checksums;

/// <summary>
/// The "strong" per-block checksum (get_checksum2, checksum.c:322) and the whole-file
/// transfer-verification digest (match_sums's sum_init/sum_update/sum_end accumulator,
/// checksum.c:576+), both for the MD5 case only -- the only algorithm this v1 negotiates
/// (see <see cref="RsyncWindows.Core.Wire.ProtocolNegotiator"/>). rsync's MD5 is standard
/// RFC1321 MD5, not a custom variant, so .NET's built-in MD5 is used directly.
///
/// Whole-file digest is always the FULL <see cref="Length"/> bytes on the wire (match.c:467
/// writes xfer_sum_len bytes); only the per-block strong checksum is SHORT --
/// <see cref="Delta.SumHead.ForFileLength"/> carries the real generator's
/// s2length-per-file-length computation.
///
/// IMPORTANT ASYMMETRY (checksum.c:615-617 vs get_checksum2's CSUM_MD5 case): the per-block
/// checksum IS seeded, but the whole-file transfer-verification digest is NOT -- sum_init()
/// only feeds the seed into the accumulator for the legacy MD4 family, never for MD5. Mixing
/// these up would make transfers "succeed" with a corrupted verification step, since both
/// sides would independently compute the same (wrong) unseeded digest and never notice.
/// </summary>
public static class StrongChecksum
{
    public const int Length = 16; // SUM_LENGTH (rsync.h:756) -- MD5 digest length

    /// <summary>SHORT_SUM_LENGTH (rsync.h:757) -- the per-block strong-checksum length every
    /// real rsync session STARTS with (io.c:82), before any digest-mismatch redo escalation.
    /// The value a real generator puts in a sum-head's s2length field is derived from this
    /// (plus BLOCKSUM_BIAS) by sum_sizes_sqroot(), not from <see cref="Length"/> -- see
    /// <see cref="Delta.SumHead.ForFileLength"/>, which mirrors that computation.</summary>
    public const int ShortSumLength = 2;

    /// <summary>The per-block signature checksum (get_checksum2's CSUM_MD5 branch,
    /// checksum.c:356-374). Prepends the 4-byte little-endian seed before the data when the
    /// seed is nonzero (matching `if (checksum_seed)` -- a zero seed contributes nothing, not
    /// even a zeroed 4-byte block) and always in "proper seed order" (seed-before-data), since
    /// <see cref="RsyncWindows.Core.Wire.ProtocolNegotiator"/> always negotiates
    /// CF_CHKSUM_SEED_FIX.</summary>
    public static byte[] ComputeBlockChecksum(int checksumSeed, ReadOnlySpan<byte> data)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (checksumSeed != 0)
        {
            Span<byte> seedBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(seedBuf, checksumSeed);
            md5.AppendData(seedBuf);
        }
        md5.AppendData(data);
        return md5.GetHashAndReset();
    }

    /// <summary>Starts the whole-file transfer-verification digest accumulator -- deliberately
    /// unseeded (see class doc comment). Feed it every literal and matched-block byte as it's
    /// sent/received, in order, then call <c>GetHashAndReset()</c>.</summary>
    public static IncrementalHash CreateWholeFileDigest() => IncrementalHash.CreateHash(HashAlgorithmName.MD5);
}
