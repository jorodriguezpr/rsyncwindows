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

public class WireCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Int32_RoundTrips(int value)
    {
        using var ms = new MemoryStream();
        WireCodec.WriteInt32(ms, value);
        ms.Position = 0;
        Assert.Equal(value, WireCodec.ReadInt32(ms));
    }

    // Hand-derived byte-exact vectors (traced against write_varint's actual algorithm,
    // io.c:2349) -- these catch a systematic sign/shift error that a pure round-trip test
    // (encode-then-decode with the same, possibly-symmetrically-wrong code) would miss.
    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x80 })]
    [InlineData(-1, new byte[] { 0xF0, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void VarInt_MatchesKnownByteLayout(int value, byte[] expectedBytes)
    {
        using var ms = new MemoryStream();
        WireCodec.WriteVarInt(ms, value);
        Assert.Equal(expectedBytes, ms.ToArray());

        ms.Position = 0;
        Assert.Equal(value, WireCodec.ReadVarInt(ms));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(1_000_000)]
    [InlineData(-1_000_000)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue + 1)]
    [InlineData(int.MaxValue - 1)]
    public void VarInt_RoundTrips(int value)
    {
        using var ms = new MemoryStream();
        WireCodec.WriteVarInt(ms, value);
        ms.Position = 0;
        Assert.Equal(value, WireCodec.ReadVarInt(ms));
    }

    [Theory]
    [InlineData(0L, (byte)3)]
    [InlineData(1L, (byte)3)]
    [InlineData(-1L, (byte)3)]
    [InlineData(long.MaxValue, (byte)3)]
    [InlineData(long.MinValue, (byte)3)]
    [InlineData(0L, (byte)8)]
    [InlineData(4_294_967_296L, (byte)3)] // 2^32, needs more than 4 bytes
    [InlineData(-4_294_967_296L, (byte)3)]
    public void VarLong_RoundTrips(long value, byte minBytes)
    {
        using var ms = new MemoryStream();
        WireCodec.WriteVarLong(ms, value, minBytes);
        ms.Position = 0;
        Assert.Equal(value, WireCodec.ReadVarLong(ms, minBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(32767)] // 0x7FFF -- the largest length write_vstring allows
    public void VString_RoundTrips(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);

        using var ms = new MemoryStream();
        WireCodec.WriteVString(ms, data);
        ms.Position = 0;
        Assert.Equal(data, WireCodec.ReadVString(ms, maxLen: 40000));
    }

    [Fact]
    public void VString_RejectsOverLongPayload()
    {
        using var ms = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => WireCodec.WriteVString(ms, new byte[0x8000]));
    }
}
