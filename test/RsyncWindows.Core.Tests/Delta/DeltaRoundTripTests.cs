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
/// End-to-end delta-transfer round trips: a "basis" (receiver's existing file) and a "new"
/// (sender's file) byte array are run through the real checksum-table + hash-search + token
/// stream pipeline, and the reconstructed output is asserted byte-exact against the new data,
/// with the whole-file digest also required to match -- this is the plan's stated Phase 3
/// gate ("whole-file MD5 matches sender vs. receiver for all mutation scenarios").
/// </summary>
public class DeltaRoundTripTests
{
    private const int ChecksumSeed = 0x2A2A2A2A;

    private static (byte[] Reconstructed, bool DigestMatches) RunDeltaTransfer(byte[] basisData, byte[] newData)
    {
        using var checksumWire = new MemoryStream();
        ChecksumTable.ComputeAndWrite(basisData, ChecksumSeed, checksumWire);
        checksumWire.Position = 0;
        var table = ChecksumTable.ReadFromWire(checksumWire);

        using var tokenWire = new MemoryStream();
        byte[] senderDigest = DeltaSender.MatchAndSend(newData, table, ChecksumSeed, tokenWire);
        tokenWire.Position = 0;

        var result = DeltaReceiver.Reconstruct(basisData, table.Head, tokenWire);
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
        var (reconstructed, digestMatches) = RunDeltaTransfer(data, (byte[])data.Clone());
        Assert.Equal(data, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void CompletelyDifferentFiles_RoundTrips_AllLiteral()
    {
        var basis = RandomBytes(30_000, seed: 1);
        var newData = RandomBytes(30_000, seed: 2);
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void AppendedData_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        var newData = basis.Concat(RandomBytes(5_000, seed: 99)).ToArray();
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void PrependedData_RoundTrips()
    {
        // Inserting bytes at the START shifts every subsequent block's alignment -- this is
        // exactly the case the rolling checksum search exists to still find efficiently.
        var basis = RandomBytes(20_000);
        var newData = RandomBytes(1_234, seed: 77).Concat(basis).ToArray();
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void DataInsertedInTheMiddle_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        var inserted = RandomBytes(555, seed: 88);
        var newData = basis[..10_000].Concat(inserted).Concat(basis[10_000..]).ToArray();
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void DataDeletedFromTheMiddle_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        var newData = basis[..8_000].Concat(basis[8_500..]).ToArray();
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void Truncated_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        var newData = basis[..7_777].ToArray();
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void ByteFlipAtBlockBoundary_RoundTrips()
    {
        var basis = RandomBytes(20_000);
        int blockLen = RsyncWindows.Core.Checksums.BlockSizer.ComputeBlockLength(basis.Length);
        var newData = (byte[])basis.Clone();
        // Flip the very first byte of the second block -- exercises a single-block mismatch
        // surrounded by matches on both sides.
        newData[blockLen] ^= 0xFF;
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void ByteFlipEveryBlockBoundary_RoundTrips()
    {
        var basis = RandomBytes(50_000);
        int blockLen = RsyncWindows.Core.Checksums.BlockSizer.ComputeBlockLength(basis.Length);
        var newData = (byte[])basis.Clone();
        for (int offset = 0; offset < newData.Length; offset += blockLen)
            newData[offset] ^= 0xFF;
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void EmptyBasisFile_RoundTrips_AllLiteral()
    {
        var newData = RandomBytes(10_000);
        var (reconstructed, digestMatches) = RunDeltaTransfer([], newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void EmptyNewFile_RoundTrips_ToEmpty()
    {
        var basis = RandomBytes(10_000);
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, []);
        Assert.Empty(reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void BothFilesEmpty_RoundTrips()
    {
        var (reconstructed, digestMatches) = RunDeltaTransfer([], []);
        Assert.Empty(reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void SingleByteFiles_RoundTrip()
    {
        var (reconstructed, digestMatches) = RunDeltaTransfer([0x42], [0x42]);
        Assert.Equal(new byte[] { 0x42 }, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void SmallerThanOneBlock_RoundTrips()
    {
        var basis = RandomBytes(300);
        var newData = (byte[])basis.Clone();
        newData[150] ^= 0xFF;
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }

    [Fact]
    public void RepeatingContent_HighCollisionRate_StillRoundTrips()
    {
        // Every block has an IDENTICAL weak+strong checksum -- exercises the hash-chain walk
        // and MAX_CHAIN_LEN cap path, not just the common single-candidate case.
        var basis = new byte[70_000];
        for (int i = 0; i < basis.Length; i++)
            basis[i] = (byte)(i % 251); // a repeating, non-trivial pattern
        var newData = basis[100..].Concat(new byte[100]).ToArray(); // shifted, still highly repetitive
        var (reconstructed, digestMatches) = RunDeltaTransfer(basis, newData);
        Assert.Equal(newData, reconstructed);
        Assert.True(digestMatches);
    }
}
