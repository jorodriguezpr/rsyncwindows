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

using System.Buffers.Binary;

namespace RsyncWindows.Core.Wire;

/// <summary>
/// Byte-exact port of rsync's io.c integer/string wire primitives (write_int/read_int,
/// write_varint/read_varint, write_varlong/read_varlong, write_vstring/read_vstring).
/// These are transliterated directly from the C implementation rather than "cleaned up",
/// because wire-compatibility with real rsync depends on matching the exact byte layout,
/// not just an equivalent variable-length integer scheme.
/// </summary>
public static class WireCodec
{
    // ---- fixed 4-byte little-endian int (write_int/read_int, io.c) ----

    public static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    // ---- fixed 2-byte little-endian unsigned int (write_shortint/read_shortint, io.c:2334) ----

    public static void WriteInt16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        s.Write(b);
    }

    public static ushort ReadInt16(Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public static int ReadInt32(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    // ---- variable-length int, 1-5 bytes (write_varint/read_varint, io.c:2349/1986) ----
    //
    // int_byte_extra[ch/4] (io.c:167) gives the number of extra bytes implied by the
    // leading byte's high bits: 0 for ch<0x80, 1 for 0x80-0xBF, 2 for 0xC0-0xEF (mostly),
    // up to 6 for 0xFC-0xFF (5-byte varint carries up to sizeof(int32)+1=5 total bytes on
    // the wire including the marker byte, per the "if (extra >= sizeof u.b)" overflow guard
    // in read_varint using a 5-byte buffer).
    private static readonly byte[] IntByteExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 6,
    };

    public static void WriteVarInt(Stream s, int x)
    {
        // b[1..4] = x as little-endian bytes; b[0] is the marker byte, filled in below.
        Span<byte> b = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(b.Slice(1, 4), x);

        int cnt = 4;
        while (cnt > 1 && b[cnt] == 0)
            cnt--;
        byte bit = (byte)(1 << (8 - cnt));

        if (b[cnt] >= bit)
        {
            cnt++;
            b[0] = (byte)~(bit - 1);
        }
        else if (cnt > 1)
        {
            b[0] = (byte)(b[cnt] | ~(bit * 2 - 1));
        }
        else
        {
            b[0] = b[1];
        }

        s.Write(b[..cnt]);
    }

    public static int ReadVarInt(Stream s)
    {
        Span<byte> u = stackalloc byte[5];
        byte ch = ReadByte(s);
        int extra = IntByteExtra[ch / 4];
        if (extra > 0)
        {
            if (extra >= u.Length)
                throw new InvalidDataException("Overflow in ReadVarInt()");
            byte bit = (byte)(1 << (8 - extra));
            s.ReadExactly(u[..extra]);
            u[extra] = (byte)(ch & (bit - 1));
        }
        else
        {
            u[0] = ch;
        }
        return BinaryPrimitives.ReadInt32LittleEndian(u[..4]);
    }

    // ---- variable-length 64-bit int with a minimum byte count (write_varlong/read_varlong,
    // io.c:2371/2018) — used for fields like mtime/size that need a guaranteed minimum width. ----

    public static void WriteVarLong(Stream s, long x, byte minBytes)
    {
        Span<byte> b = stackalloc byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(b.Slice(1, 8), x);

        int cnt = 8;
        while (cnt > minBytes && b[cnt] == 0)
            cnt--;
        byte bit = (byte)(1 << (7 - cnt + minBytes));

        if (b[cnt] >= bit)
        {
            cnt++;
            b[0] = (byte)~(bit - 1);
        }
        else if (cnt > minBytes)
        {
            b[0] = (byte)(b[cnt] | ~(bit * 2 - 1));
        }
        else
        {
            b[0] = b[cnt];
        }

        s.Write(b[..cnt]);
    }

    public static long ReadVarLong(Stream s, byte minBytes)
    {
        Span<byte> u = stackalloc byte[9];
        Span<byte> b2 = stackalloc byte[8];
        s.ReadExactly(b2[..minBytes]);
        b2[1..minBytes].CopyTo(u[..(minBytes - 1)]);

        int extra = IntByteExtra[b2[0] / 4];
        if (extra > 0)
        {
            byte bit = (byte)(1 << (8 - extra));
            if (minBytes + extra > u.Length)
                throw new InvalidDataException("Overflow in ReadVarLong()");
            s.ReadExactly(u.Slice(minBytes - 1, extra));
            u[minBytes + extra - 1] = (byte)(b2[0] & (bit - 1));
        }
        else
        {
            u[minBytes - 1] = b2[0];
        }
        return BinaryPrimitives.ReadInt64LittleEndian(u[..8]);
    }

    // ---- Pascal-style length-prefixed string, 1 or 2 byte length (write_vstring/read_vstring,
    // io.c:2482/2174). Used for negotiated algorithm-name lists and similar short strings. ----

    public static void WriteVString(Stream s, ReadOnlySpan<byte> data)
    {
        if (data.Length > 0x7FFF)
            throw new InvalidDataException($"attempting to send over-long vstring ({data.Length} > {0x7FFF})");

        if (data.Length > 0x7F)
        {
            Span<byte> lenBuf = stackalloc byte[2];
            lenBuf[0] = (byte)(data.Length / 0x100 + 0x80);
            lenBuf[1] = (byte)data.Length;
            s.Write(lenBuf);
        }
        else
        {
            s.WriteByte((byte)data.Length);
        }
        if (data.Length > 0)
            s.Write(data);
    }

    public static byte[] ReadVString(Stream s, int maxLen = 32768)
    {
        int len = ReadByte(s);
        if ((len & 0x80) != 0)
            len = (len & ~0x80) * 0x100 + ReadByte(s);

        if (len >= maxLen)
            throw new InvalidDataException($"over-long vstring received ({len} >= {maxLen})");

        if (len == 0)
            return [];
        var buf = new byte[len];
        s.ReadExactly(buf);
        return buf;
    }

    internal static byte ReadByte(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0)
            throw new EndOfStreamException();
        return (byte)b;
    }
}
