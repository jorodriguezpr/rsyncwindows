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
using Xunit;

namespace RsyncWindows.Core.Tests.Checksums;

public class BlockSizerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(700)]
    [InlineData(490_000)] // exactly BLOCK_SIZE * BLOCK_SIZE
    public void SmallFiles_UseFlatBlockSize(long length)
    {
        Assert.Equal(700, BlockSizer.ComputeBlockLength(length));
    }

    [Fact]
    public void JustOverThreshold_UsesSquareRootApproximation()
    {
        // Right at the threshold the sqrt-approximation's raw output can still land below the
        // BLOCK_SIZE=700 floor, in which case MAX(_, 700) makes the floor itself the answer --
        // and 700 isn't a multiple of 8, so only assert the floor is respected here.
        int b = BlockSizer.ComputeBlockLength(490_001);
        Assert.True(b >= 700);
    }

    [Fact]
    public void WellAboveThreshold_RoundsToMultipleOf8()
    {
        // Far enough past the floor that the sqrt-approximation's own output dominates --
        // the loop only ever sets bits at position >=3, so the result is always a multiple of 8.
        int b = BlockSizer.ComputeBlockLength(10_000_000);
        Assert.True(b > 700);
        Assert.Equal(0, b % 8);
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(100_000_000)]
    [InlineData(10_000_000_000)]
    public void LargerFiles_BlockLengthApproximatesSquareRoot(long length)
    {
        int b = BlockSizer.ComputeBlockLength(length);
        double sqrtLen = Math.Sqrt(length);
        // Loose bound -- this is a rounded/truncated approximation, not an exact sqrt.
        Assert.InRange(b, sqrtLen * 0.5, sqrtLen * 2.5);
    }

    [Fact]
    public void HugeFile_CapsAtMaxBlockSize()
    {
        int b = BlockSizer.ComputeBlockLength(long.MaxValue / 2);
        Assert.Equal(1 << 17, b);
    }
}
