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

public class CompressedTokenStreamTests
{
    private static readonly byte[][] Basis =
    [
        System.Text.Encoding.UTF8.GetBytes(new string('A', 700)),
        System.Text.Encoding.UTF8.GetBytes(new string('B', 700)),
        System.Text.Encoding.UTF8.GetBytes(new string('C', 700)),
    ];

    [Fact]
    public void LiteralOnly_RoundTrips()
    {
        byte[] literal = System.Text.Encoding.UTF8.GetBytes("Hello compressed rsyncWindows world, this text should compress reasonably well since it repeats reasonably well reasonably well.");

        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
            writer.WriteFinalLiteralAndEnd(ms, literal);
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var collected = new List<byte>();
        while (true)
        {
            var token = reader.Read(ms);
            if (token.IsEnd)
                break;
            Assert.NotNull(token.Literal);
            collected.AddRange(token.Literal!);
        }

        Assert.Equal(literal, collected.ToArray());
    }

    [Fact]
    public void SingleMatchedBlock_NoLiteral_RoundTrips()
    {
        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
        {
            writer.WriteLiteralAndToken(ms, [], 0);
            writer.WriteFinalLiteralAndEnd(ms, []);
        }
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var first = reader.Read(ms);
        Assert.Equal(0, first.BlockIndex);
        reader.PrimeFromMatchedBlock(Basis[0]);

        var end = reader.Read(ms);
        Assert.True(end.IsEnd);
    }

    [Fact]
    public void ConsecutiveMatchedBlocks_EncodedAsRun_RoundTrips()
    {
        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
        {
            writer.WriteLiteralAndToken(ms, [], 0);
            writer.WriteLiteralAndToken(ms, [], 1);
            writer.WriteLiteralAndToken(ms, [], 2);
            writer.WriteFinalLiteralAndEnd(ms, []);
        }
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var indices = new List<int>();
        while (true)
        {
            var token = reader.Read(ms);
            if (token.IsEnd)
                break;
            Assert.NotNull(token.BlockIndex);
            indices.Add(token.BlockIndex!.Value);
            reader.PrimeFromMatchedBlock(Basis[token.BlockIndex!.Value]);
        }

        Assert.Equal([0, 1, 2], indices);
    }

    [Fact]
    public void MixedLiteralsAndMatches_RoundTrips()
    {
        byte[] lit1 = System.Text.Encoding.UTF8.GetBytes("some inserted bytes here that were not in the basis file at all");
        byte[] lit2 = System.Text.Encoding.UTF8.GetBytes("more new content at the tail end of the file");

        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
        {
            writer.WriteLiteralAndToken(ms, lit1, 0);
            writer.PrimeFromMatchedBlock(Basis[0]);
            writer.WriteLiteralAndToken(ms, [], 2); // non-consecutive with block 0 -- own run
            writer.PrimeFromMatchedBlock(Basis[2]);
            writer.WriteFinalLiteralAndEnd(ms, lit2);
        }
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var events = new List<(byte[]? Literal, int? Block)>();
        while (true)
        {
            var token = reader.Read(ms);
            if (token.IsEnd)
                break;
            if (token.BlockIndex is { } idx)
                reader.PrimeFromMatchedBlock(Basis[idx]);
            events.Add((token.Literal, token.BlockIndex));
        }

        // Reconstruct: literal(lit1), block(0), block(2), literal(lit2)
        Assert.True(events.Count >= 4);
        Assert.Equal(lit1, events[0].Literal);
        Assert.Equal(0, events[1].Block);
        Assert.Equal(2, events[2].Block);
        Assert.Equal(lit2, events[^1].Literal);
    }

    [Fact]
    public void LargeIncompressibleLiteral_SpansMultipleChunks_RoundTrips()
    {
        var rnd = new Random(42);
        byte[] literal = new byte[40_000]; // exceeds MaxDataCount (16383) worth of compressed output for random data
        rnd.NextBytes(literal);

        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
            writer.WriteFinalLiteralAndEnd(ms, literal);
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var collected = new List<byte>();
        while (true)
        {
            var token = reader.Read(ms);
            if (token.IsEnd)
                break;
            collected.AddRange(token.Literal!);
        }

        Assert.Equal(literal, collected.ToArray());
    }

    [Fact]
    public void EmptyFile_RoundTrips()
    {
        using var ms = new MemoryStream();
        using (var writer = new CompressedTokenWriter())
            writer.WriteFinalLiteralAndEnd(ms, []);
        ms.Position = 0;

        using var reader = new CompressedTokenReader();
        var token = reader.Read(ms);
        Assert.True(token.IsEnd);
    }
}
