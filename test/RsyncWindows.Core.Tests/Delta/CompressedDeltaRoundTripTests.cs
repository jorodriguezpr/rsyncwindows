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

using RsyncWindows.Core.Delta;
using Xunit;

namespace RsyncWindows.Core.Tests.Delta;

/// <summary>
/// The same end-to-end delta-transfer round trip <see cref="DeltaRoundTripTests"/> exercises,
/// but with <see cref="DeltaSender.MatchAndSend"/>/<see cref="DeltaReceiver.Reconstruct"/> wired
/// to a <see cref="CompressedTokenWriter"/>/<see cref="CompressedTokenReader"/> pair (`-z`) --
/// this is the regression net for the symmetric writer/reader history-priming fix
/// (<see cref="CompressedTokenWriter.PrimeFromMatchedBlock"/>): unlike
/// <see cref="CompressedTokenStreamTests"/>'s hand-built literal/match sequences, these scenarios
/// run real basis/new-file byte patterns through the actual hash-search, so any priming-offset
/// regression would surface as a literal decoding to the wrong bytes, not just a digest mismatch.
/// </summary>
public class CompressedDeltaRoundTripTests
{
    private const int ChecksumSeed = 0x2A2A2A2A;

    private static (byte[] Reconstructed, bool DigestMatches) RunCompressedDeltaTransfer(byte[] basisData, byte[] newData)
    {
        using var checksumWire = new MemoryStream();
        ChecksumTable.ComputeAndWrite(basisData, ChecksumSeed, checksumWire);
        checksumWire.Position = 0;
        var table = ChecksumTable.ReadFromWire(checksumWire);

        using var tokenWire = new MemoryStream();
        byte[] senderDigest;
        using (var writer = new CompressedTokenWriter())
            senderDigest = DeltaSender.MatchAndSend(newData, table, ChecksumSeed, tokenWire, writer);
        tokenWire.Position = 0;

        DeltaResult result;
        using (var reader = new CompressedTokenReader())
            result = DeltaReceiver.Reconstruct(basisData, table.Head, tokenWire, reader);

        Assert.Equal(Convert.ToHexString(senderDigest), Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(newData)));
        return (result.Reconstructed, result.DigestMatches);
    }

    private static byte[] RandomBytes(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void IdenticalFiles_RoundTrips_AllMatched()
    {
        var data = RandomBytes(50_000);
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(data, (byte[])data.Clone());
        Assert.Equal(data, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void DataInsertedInTheMiddle_RoundTrips()
    {
        // literal -> match -> literal(inserted) -> match -- exactly the sequence that exposed
        // the write-side priming gap (a literal after a matched block decoding against the
        // wrong window offset) before CompressedTokenWriter.PrimeFromMatchedBlock was added.
        var basis = RandomBytes(20_000);
        var inserted = RandomBytes(555, seed: 88);
        var newData = basis[..10_000].Concat(inserted).Concat(basis[10_000..]).ToArray();
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void DataDeletedFromTheMiddle_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        var newData = basis[..8_000].Concat(basis[8_500..]).ToArray();
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void ByteFlipEveryBlockBoundary_RoundTrips()
    {
        // Many short literal runs interleaved with many matches -- exercises repeated
        // literal/match/literal/match transitions, not just one or two.
        var basis = RandomBytes(50_000);
        int blockLen = RsyncWindows.Core.Checksums.BlockSizer.ComputeBlockLength(basis.Length);
        var newData = (byte[])basis.Clone();
        for (int offset = 0; offset < newData.Length; offset += blockLen)
            newData[offset] ^= 0xFF;
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void CompletelyDifferentFiles_RoundTrips_AllLiteral()
    {
        var basis = RandomBytes(30_000, seed: 1);
        var newData = RandomBytes(30_000, seed: 2);
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void RepeatingContent_HighCollisionRate_StillRoundTrips()
    {
        // This scenario (a long run of consecutive matched blocks, then a literal, then another
        // long run) is what surfaced a real bug in a prior SharpZipLib-based revision of
        // CompressedTokenStream: Deflater.Flush() there corrupted any flush sequence where an
        // earlier flush happened to pick a raw STORED block internally. Root-caused and fixed by
        // moving to a P/Invoke wrapper around real zlib (RawDeflater/RawInflater, via
        // System.IO.Compression.Native) -- genuine zlib's Z_SYNC_FLUSH doesn't have that gap,
        // which is exactly why real rsync's own C source can rely on this same
        // sync-flush-and-discard priming technique. See [[project_rsyncwindows_phase6_compression_inprogress]].
        var basis = new byte[70_000];
        for (int i = 0; i < basis.Length; i++)
            basis[i] = (byte)(i % 251);
        var newData = basis[100..].Concat(new byte[100]).ToArray();
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void EmptyBasisFile_RoundTrips_AllLiteral()
    {
        var newData = RandomBytes(10_000);
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer([], newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void EmptyNewFile_RoundTrips_ToEmpty()
    {
        var basis = RandomBytes(10_000);
        var (reconstructed, digestMatches) = RunCompressedDeltaTransfer(basis, []);
        Assert.Empty(reconstructed);
        Assert.True(digestMatches);
    }
}
