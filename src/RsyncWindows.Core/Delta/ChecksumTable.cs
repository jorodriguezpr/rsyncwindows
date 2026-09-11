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
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Core.Delta;

public sealed record BlockChecksum(int Index, int Offset, int Length, uint Weak, byte[] Strong);

/// <summary>The full set of per-block checksums for one file, plus the hash-table bucketing
/// used to search for matches -- ports build_hash_table() (match.c:78) and its use of
/// generate_and_send_sums()'s wire format (generator.c:800-826: `write_int(sum1); write_buf(sum2)`
/// per block, after the <see cref="SumHead"/>).</summary>
public sealed class ChecksumTable
{
    private const int TraditionalTableSize = 1 << 16;

    public IReadOnlyList<BlockChecksum> Blocks { get; }
    public SumHead Head { get; }

    private readonly int[] _hashTable;
    private readonly int[] _chain;
    private readonly uint _tableSize;

    private ChecksumTable(SumHead head, IReadOnlyList<BlockChecksum> blocks)
    {
        Head = head;
        Blocks = blocks;

        // Dynamic table sizing so the hash load for big files is ~80% (match.c:83-88).
        _tableSize = (uint)(blocks.Count / 8) * 10 + 11;
        if (_tableSize < TraditionalTableSize)
            _tableSize = TraditionalTableSize;

        _hashTable = new int[_tableSize];
        Array.Fill(_hashTable, -1);
        _chain = new int[blocks.Count];

        bool traditional = _tableSize == TraditionalTableSize;
        for (int i = 0; i < blocks.Count; i++)
        {
            uint packed = RollingChecksum.Pack(blocks[i].Weak & 0xFFFF, blocks[i].Weak >> 16);
            uint bucket = traditional ? SumHash2(blocks[i].Weak & 0xFFFF, blocks[i].Weak >> 16) : packed % _tableSize;
            _chain[i] = _hashTable[bucket];
            _hashTable[bucket] = i;
        }
    }

    private static uint SumHash2(uint s1, uint s2) => (s1 + s2) & 0xFFFF;

    /// <summary>Looks up the hash bucket for a packed weak-checksum value, returning the
    /// first candidate block index (-1 if none), to be walked via <see cref="NextInChain"/>.</summary>
    public int FirstInBucket(uint weakSum)
    {
        bool traditional = _tableSize == TraditionalTableSize;
        uint s1 = weakSum & 0xFFFF, s2 = weakSum >> 16;
        uint bucket = traditional ? SumHash2(s1, s2) : weakSum % _tableSize;
        return _hashTable[bucket];
    }

    public int NextInChain(int blockIndex) => _chain[blockIndex];

    /// <summary>Reads a checksum table off the wire (the generator side's output) --
    /// counterpart to <see cref="ComputeFromBasisFile"/>.</summary>
    public static ChecksumTable ReadFromWire(Stream s)
    {
        var head = SumHead.Read(s);
        var blocks = new BlockChecksum[head.Count];
        int offset = 0;
        for (int i = 0; i < head.Count; i++)
        {
            int length = i == head.Count - 1 && head.Remainder != 0 ? head.Remainder : head.BlockLength;
            uint weak = unchecked((uint)WireCodec.ReadInt32(s));
            var strong = new byte[head.S2Length];
            s.ReadExactly(strong);
            blocks[i] = new BlockChecksum(i, offset, length, weak, strong);
            offset += length;
        }
        return new ChecksumTable(head, blocks);
    }

    /// <summary>Computes the checksum table for a basis file already fully read into memory
    /// (see the Delta namespace's doc comment on the in-memory scope of this v1) and writes it
    /// to <paramref name="output"/> -- ports generate_and_send_sums() (generator.c:780).</summary>
    public static ChecksumTable ComputeAndWrite(ReadOnlySpan<byte> basisData, int checksumSeed, Stream output)
    {
        var head = SumHead.ForFileLength(basisData.Length);
        head.Write(output);

        var blocks = new BlockChecksum[head.Count];
        int offset = 0;
        for (int i = 0; i < head.Count; i++)
        {
            int length = i == head.Count - 1 && head.Remainder != 0 ? head.Remainder : head.BlockLength;
            var blockData = basisData.Slice(offset, length);
            var (s1, s2) = RollingChecksum.Compute(blockData);
            uint weak = RollingChecksum.Pack(s1, s2);
            byte[] strong = StrongChecksum.ComputeBlockChecksum(checksumSeed, blockData);

            WireCodec.WriteInt32(output, unchecked((int)weak));
            // Exactly head.S2Length bytes per block -- the count a real peer derives for the
            // same length (see SumHead.ForFileLength). The digest is 16 bytes; only the first
            // head.S2Length bytes go on the wire, matching generate_and_send_sums().
            output.Write(strong, 0, head.S2Length);

            blocks[i] = new BlockChecksum(i, offset, length, weak, strong);
            offset += length;
        }
        return new ChecksumTable(head, blocks);
    }
}
