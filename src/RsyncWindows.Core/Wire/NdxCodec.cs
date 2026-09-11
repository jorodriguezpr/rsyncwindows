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

namespace RsyncWindows.Core.Wire;

/// <summary>Sentinel index values (rsync.h:315-318) used by <see cref="NdxEncoder"/>/<see cref="NdxDecoder"/>.</summary>
public static class Ndx
{
    public const int Done = -1;
    public const int FlistEof = -2;
    public const int DelStats = -3;
    public const int FlistOffset = -101;
}

/// <summary>
/// Byte-reduction file-list-index encoder, ported from write_ndx (io.c:2503). This is
/// STATEFUL — it diffs each index against the previous one of the same sign — so a single
/// instance must be dedicated to one direction of one connection, never shared or reused
/// across roles/connections.
/// </summary>
public sealed class NdxEncoder
{
    private int _prevPositive = -1;
    private int _prevNegative = 1;

    public void Write(Stream s, int ndx)
    {
        Span<byte> b = stackalloc byte[6];
        int cnt = 0;
        int diff;

        if (ndx >= 0)
        {
            diff = ndx - _prevPositive;
            _prevPositive = ndx;
        }
        else if (ndx == Ndx.Done)
        {
            s.WriteByte(0);
            return;
        }
        else
        {
            b[cnt++] = 0xFF;
            ndx = -ndx;
            diff = ndx - _prevNegative;
            _prevNegative = ndx;
        }

        // A diff of 1-253 is a one-byte diff; 254-32767 (or <=0) is 0xFE + 2-byte diff;
        // otherwise 0xFE + all 4 bytes of the (non-negative) index with the high bit set.
        if (diff is < 0xFE and > 0)
        {
            b[cnt++] = (byte)diff;
        }
        else if (diff < 0 || diff > 0x7FFF)
        {
            b[cnt++] = 0xFE;
            b[cnt++] = (byte)((ndx >> 24) | 0x80);
            b[cnt++] = (byte)ndx;
            b[cnt++] = (byte)(ndx >> 8);
            b[cnt++] = (byte)(ndx >> 16);
        }
        else
        {
            b[cnt++] = 0xFE;
            b[cnt++] = (byte)(diff >> 8);
            b[cnt++] = (byte)diff;
        }

        s.Write(b[..cnt]);
    }
}

/// <summary>Counterpart to <see cref="NdxEncoder"/>, ported from read_ndx (io.c:2550).</summary>
public sealed class NdxDecoder
{
    private int _prevPositive = -1;
    private int _prevNegative = 1;

    public int Read(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        bool negative = false;

        s.ReadExactly(b[..1]);
        if (b[0] == 0xFF)
        {
            negative = true;
            s.ReadExactly(b[..1]);
        }
        else if (b[0] == 0)
        {
            return Ndx.Done;
        }

        ref int prev = ref (negative ? ref _prevNegative : ref _prevPositive);
        uint unum;

        if (b[0] == 0xFE)
        {
            s.ReadExactly(b[..2]);
            if ((b[0] & 0x80) != 0)
            {
                byte hi = (byte)(b[0] & ~0x80);
                b[0] = b[1];
                s.ReadExactly(b[1..3]);
                b[3] = hi;
                unum = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }
            else
            {
                unum = (uint)((b[0] << 8) + b[1] + (uint)prev);
            }
        }
        else
        {
            unum = (uint)(b[0] + (uint)prev);
        }

        if (unum > int.MaxValue)
            throw new InvalidDataException($"Invalid file index: {unum}");

        int num = (int)unum;
        prev = num;
        return negative ? -num : num;
    }
}
