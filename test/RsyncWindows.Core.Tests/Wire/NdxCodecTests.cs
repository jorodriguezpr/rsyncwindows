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

using RsyncWindows.Core.Wire;
using Xunit;

namespace RsyncWindows.Core.Tests.Wire;

public class NdxCodecTests
{
    private static int[] RoundTrip(params int[] sequence)
    {
        var encoder = new NdxEncoder();
        using var ms = new MemoryStream();
        foreach (var v in sequence)
            encoder.Write(ms, v);

        ms.Position = 0;
        var decoder = new NdxDecoder();
        var result = new int[sequence.Length];
        for (int i = 0; i < sequence.Length; i++)
            result[i] = decoder.Read(ms);
        return result;
    }

    [Fact]
    public void SequentialPositiveIndices_RoundTrip()
    {
        var seq = Enumerable.Range(0, 20).ToArray();
        Assert.Equal(seq, RoundTrip(seq));
    }

    [Fact]
    public void DoneSentinel_RoundTrips()
    {
        Assert.Equal([0, 1, 2, Ndx.Done], RoundTrip(0, 1, 2, Ndx.Done));
    }

    [Fact]
    public void LargeForwardJump_RoundTrips()
    {
        // Diff of 254-32767 (0xFE + 2-byte diff path).
        Assert.Equal([0, 5000], RoundTrip(0, 5000));
    }

    [Fact]
    public void HugeJumpBeyond16Bit_RoundTrips()
    {
        // Diff > 0x7FFF forces the 0xFE + 4-byte absolute-value path.
        Assert.Equal([0, 100_000], RoundTrip(0, 100_000));
    }

    [Fact]
    public void BackwardJump_RoundTrips()
    {
        // A negative diff (index goes backward) also forces the 4-byte absolute path.
        Assert.Equal([1000, 10], RoundTrip(1000, 10));
    }

    [Fact]
    public void NegativeSentinels_RoundTrip()
    {
        Assert.Equal(
            [Ndx.FlistEof, Ndx.DelStats, Ndx.FlistOffset],
            RoundTrip(Ndx.FlistEof, Ndx.DelStats, Ndx.FlistOffset));
    }

    [Fact]
    public void MixedPositiveAndNegative_TrackIndependentState()
    {
        // Positive and negative indices maintain separate "previous value" state
        // (prev_positive / prev_negative in the C source) -- interleaving them must not
        // let one series' diff bleed into the other's.
        int[] seq = [0, -5, 1, -10, 2, -3];
        Assert.Equal(seq, RoundTrip(seq));
    }

    [Fact]
    public void RepeatedSameIndex_RoundTrips()
    {
        // A diff of exactly 0 forces the 0xFE 2-byte path (write_ndx's one-byte fast path
        // requires diff in 1..253, so a repeat is not a degenerate/invalid case).
        Assert.Equal([5, 5, 5], RoundTrip(5, 5, 5));
    }
}
