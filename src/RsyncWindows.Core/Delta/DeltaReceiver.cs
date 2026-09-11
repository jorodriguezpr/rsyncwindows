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

public sealed record DeltaResult(byte[] Reconstructed, bool DigestMatches);

/// <summary>Ports the receiver side of receive_data() (receiver.c) for reconstructing a file
/// from a basis file plus the sender's token stream: literal runs are written directly, a
/// matched-block token copies the corresponding range out of the basis file, and both feed
/// the same whole-file digest the sender accumulated, compared at the end.</summary>
public static class DeltaReceiver
{
    /// <param name="compressor">When non-null, reads the token stream through the `-z` wire
    /// format (<see cref="CompressedTokenReader"/>) instead of the plain <see cref="TokenStream"/>
    /// framing -- one instance per file. Every matched-block token primes the reader's inflate
    /// window from the basis bytes it references (<see cref="CompressedTokenReader.PrimeFromMatchedBlock"/>),
    /// which is required for correctness (not optional) whenever a real sender's compressed
    /// literal output back-references into that same region.</param>
    public static DeltaResult Reconstruct(ReadOnlySpan<byte> basisData, SumHead head, Stream tokenInput, CompressedTokenReader? compressor = null)
    {
        using var digest = StrongChecksum.CreateWholeFileDigest();
        using var output = new MemoryStream();

        while (true)
        {
            var token = compressor != null ? compressor.Read(tokenInput) : TokenStream.Read(tokenInput);
            if (token.IsEnd)
                break;

            if (token.Literal is { } literal)
            {
                output.Write(literal);
                digest.AppendData(literal);
            }
            else
            {
                int index = token.BlockIndex!.Value;
                int offset = index * head.BlockLength;
                int length = index == head.Count - 1 && head.Remainder != 0 ? head.Remainder : head.BlockLength;
                var block = basisData.Slice(offset, length);
                output.Write(block);
                digest.AppendData(block);
                compressor?.PrimeFromMatchedBlock(block);
            }
        }

        byte[] localDigest = digest.GetHashAndReset();
        var remoteDigest = new byte[StrongChecksum.Length];
        tokenInput.ReadExactly(remoteDigest);

        return new DeltaResult(output.ToArray(), localDigest.AsSpan().SequenceEqual(remoteDigest));
    }
}
