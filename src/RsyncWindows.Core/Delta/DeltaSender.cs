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

using RsyncWindows.Core.Checksums;

namespace RsyncWindows.Core.Delta;

/// <summary>
/// Ports hash_search()/matched()/match_sums() (match.c) for the sender role: scans the new
/// file's data for blocks matching the basis-file <see cref="ChecksumTable"/> the peer sent,
/// emitting a token stream (literal runs + matched-block references) and returning the
/// whole-file transfer-verification digest.
///
/// Deliberately NOT ported: the "want_i" adjacent-match hint (efficiency-only -- affects
/// which of several identical-content blocks gets referenced, never correctness) and the
/// --inplace "updating_basis_file" branches (an option this v1 doesn't expose). The
/// "recompute the window then immediately roll one more step" dance the C code does right
/// after a match (match.c:337-342, explained there as an I/O-buffering optimization) is
/// replaced with directly computing the fresh window at the correct post-match offset --
/// mathematically identical, since a roll is defined to equal a fresh computation; this
/// v1 operates on an in-memory span, so there's no I/O-buffering benefit to preserve anyway.
///
/// The literal-run/match-emit logic is inlined rather than factored into helper local
/// functions, since a `ReadOnlySpan&lt;byte&gt;` parameter can't be captured by a closure
/// (CS9108) -- there's only two call sites (mid-scan match, final flush), so the duplication
/// is small and avoids threading five extra parameters through non-capturing static locals.
/// </summary>
public static class DeltaSender
{
    private const int MaxChainLen = 1024; // match.c MAX_CHAIN_LEN

    /// <param name="compressor">When non-null, frames the token stream through the `-z` wire
    /// format (<see cref="CompressedTokenWriter"/>) instead of the plain <see cref="TokenStream"/>
    /// framing -- one instance per file, matching that class's per-file lifetime. Every matched
    /// block is fed to <see cref="CompressedTokenWriter.PrimeFromMatchedBlock"/> right after it's
    /// written, symmetric with <see cref="DeltaReceiver.Reconstruct"/>'s reader-side priming --
    /// required for correctness, not just compression ratio, per that method's doc comment.</param>
    public static byte[] MatchAndSend(ReadOnlySpan<byte> newData, ChecksumTable checksums, int checksumSeed, Stream output, CompressedTokenWriter? compressor = null)
    {
        using var digest = StrongChecksum.CreateWholeFileDigest();
        long lastMatch = 0;
        long len = newData.Length;

        if (checksums.Blocks.Count == 0 || len == 0)
        {
            var literal = newData.Slice((int)lastMatch, (int)(len - lastMatch));
            if (compressor != null)
                compressor.WriteFinalLiteralAndEnd(output, literal);
            else
                TokenStream.WriteFinalLiteralAndEnd(output, literal);
            if (literal.Length > 0)
                digest.AppendData(literal);
        }
        else
        {
            long offset = 0;
            int k = (int)Math.Min(len, checksums.Head.BlockLength);
            var (s1, s2) = RollingChecksum.Compute(newData.Slice((int)offset, k));
            long end = len + 1 - checksums.Blocks[^1].Length;

            while (offset < end)
            {
                uint packed = RollingChecksum.Pack(s1, s2);
                int candidate = checksums.FirstInBucket(packed);
                int matchedIndex = -1;
                int chainLen = 0;

                while (candidate >= 0)
                {
                    if (++chainLen > MaxChainLen)
                        break;

                    var block = checksums.Blocks[candidate];
                    int l = (int)Math.Min((long)checksums.Head.BlockLength, len - offset);
                    if (block.Weak == packed && block.Length == l)
                    {
                        var window = newData.Slice((int)offset, l);
                        byte[] strong = StrongChecksum.ComputeBlockChecksum(checksumSeed, window);
                        if (strong.AsSpan(0, checksums.Head.S2Length).SequenceEqual(block.Strong.AsSpan(0, checksums.Head.S2Length)))
                        {
                            matchedIndex = candidate;
                            break;
                        }
                    }
                    candidate = checksums.NextInChain(candidate);
                }

                if (matchedIndex >= 0)
                {
                    var literal = newData.Slice((int)lastMatch, (int)(offset - lastMatch));
                    if (compressor != null)
                        compressor.WriteLiteralAndToken(output, literal, matchedIndex);
                    else
                        TokenStream.WriteLiteralAndToken(output, literal, matchedIndex);
                    if (literal.Length > 0)
                        digest.AppendData(literal);
                    var block = checksums.Blocks[matchedIndex];
                    var matchedBytes = newData.Slice((int)offset, block.Length);
                    digest.AppendData(matchedBytes);
                    compressor?.PrimeFromMatchedBlock(matchedBytes);
                    lastMatch = offset + block.Length;

                    offset = lastMatch;
                    if (offset < len)
                    {
                        k = (int)Math.Min(len - offset, checksums.Head.BlockLength);
                        (s1, s2) = RollingChecksum.Compute(newData.Slice((int)offset, k));
                    }
                    continue;
                }

                bool more = offset + k < len;
                byte outgoing = newData[(int)offset];
                byte? incoming = more ? newData[(int)(offset + k)] : null;
                RollingChecksum.Roll(ref s1, ref s2, ref k, outgoing, incoming);
                offset++;
            }

            var finalLiteral = newData.Slice((int)lastMatch, (int)(len - lastMatch));
            if (compressor != null)
                compressor.WriteFinalLiteralAndEnd(output, finalLiteral);
            else
                TokenStream.WriteFinalLiteralAndEnd(output, finalLiteral);
            if (finalLiteral.Length > 0)
                digest.AppendData(finalLiteral);
        }

        byte[] finalDigest = digest.GetHashAndReset();
        output.Write(finalDigest); // write_buf(f, sender_file_sum, xfer_sum_len) -- match_sums(), match.c:467
        return finalDigest;
    }
}
