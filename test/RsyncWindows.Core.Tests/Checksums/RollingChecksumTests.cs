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

public class RollingChecksumTests
{
    [Fact]
    public void Compute_EmptyData_IsZero()
    {
        var (s1, s2) = RollingChecksum.Compute([]);
        Assert.Equal(0u, s1);
        Assert.Equal(0u, s2);
    }

    [Fact]
    public void Compute_SingleByte_MatchesHandComputation()
    {
        // s1 = byte value (as signed), s2 = s1 (one iteration of the running sum).
        var (s1, s2) = RollingChecksum.Compute([100]);
        Assert.Equal(100u, s1);
        Assert.Equal(100u, s2);
    }

    [Fact]
    public void Compute_NegativeSignedByte_WrapsCorrectly()
    {
        // 0xFF as a signed byte is -1; s1 accumulates as (uint)(-1) = 0xFFFFFFFF, masked to 16 bits.
        var (s1, _) = RollingChecksum.Compute([0xFF]);
        Assert.Equal(0xFFFFu, s1); // -1 & 0xFFFF
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(700)]
    [InlineData(4096)]
    public void Roll_MatchesFreshComputation_AcrossSlidingWindow(int windowSize)
    {
        var rnd = new Random(1234);
        var data = new byte[windowSize + 50];
        rnd.NextBytes(data);

        var (s1, s2) = RollingChecksum.Compute(data.AsSpan(0, windowSize));
        int k = windowSize;

        for (int offset = 0; offset + windowSize < data.Length; offset++)
        {
            bool more = offset + k < data.Length;
            byte outgoing = data[offset];
            byte? incoming = more ? data[offset + k] : null;
            RollingChecksum.Roll(ref s1, ref s2, ref k, outgoing, incoming);

            int newOffset = offset + 1;
            var (expectedS1, expectedS2) = RollingChecksum.Compute(data.AsSpan(newOffset, k));
            Assert.Equal(expectedS1, s1 & 0xFFFF);
            Assert.Equal(expectedS2, s2 & 0xFFFF);
        }
    }

    [Fact]
    public void Pack_CombinesS1AndS2IntoDistinctHalves()
    {
        uint packed = RollingChecksum.Pack(0x1234, 0x5678);
        Assert.Equal(0x1234u, packed & 0xFFFF);
        Assert.Equal(0x5678u, packed >> 16);
    }
}
