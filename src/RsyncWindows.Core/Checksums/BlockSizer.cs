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

/// <summary>Ports sum_sizes_sqroot()'s block-length half (generator.c:703-749). The
/// checksum-length half of that same C function lives in
/// <see cref="Delta.SumHead.ForFileLength"/> (it depends on the file length too, not just the
/// block length, so it can't be split off by block size alone).</summary>
public static class BlockSizer
{
    private const int BlockSize = 700; // rsync.h BLOCK_SIZE
    private const int MaxBlockSize = 1 << 17; // rsync.h MAX_BLOCK_SIZE (protocol >= 30)

    /// <summary>Computes the per-block length for a file of the given size: a rounded
    /// square root of the file length, floored at <see cref="BlockSize"/> and capped at
    /// <see cref="MaxBlockSize"/>, always a multiple of 8 above the floor.</summary>
    public static int ComputeBlockLength(long fileLength)
    {
        if (fileLength <= (long)BlockSize * BlockSize)
            return BlockSize;

        long l = fileLength;
        int c;
        for (c = 1; (l >>= 2) != 0; c <<= 1) { }

        if (c < 0 || c >= MaxBlockSize)
            return MaxBlockSize;

        int blength = 0;
        do
        {
            blength |= c;
            if (fileLength < (long)blength * blength)
                blength &= ~c;
            c >>= 1;
        } while (c >= 8);

        return Math.Max(blength, BlockSize);
    }
}
