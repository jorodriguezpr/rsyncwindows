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
/// Write side of rsync's multiplexed I/O framing (io.c, io_start_multiplex_out /
/// send_msg / MPLEX_BASE). Every frame is a 4-byte little-endian header
/// (top byte = MPLEX_BASE + msgcode, low 3 bytes = payload length) followed by the
/// payload. Plain <see cref="Write"/> calls are framed as MSG_DATA; <see cref="WriteMessage"/>
/// sends any other message code (INFO/WARNING/ERROR/STATS/...).
/// </summary>
public sealed class MultiplexWriteStream(Stream inner) : Stream
{
    // Comfortably under the 24-bit (0xFFFFFF / 16MB) wire limit; keeps individual frames a
    // reasonable size rather than requiring one giant frame for a large write. The exact
    // chunk boundaries don't need to match a real rsync's own buffering choices for wire
    // compatibility — a peer just concatenates MSG_DATA payloads as it demuxes them.
    private const int MaxChunk = 0x10000;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, MaxChunk);
            WriteFrame(MsgCode.Data, buffer[..n]);
            buffer = buffer[n..];
        }
    }

    /// <summary>Sends a non-data protocol message (e.g. MSG_INFO, MSG_ERROR, MSG_STATS).</summary>
    public void WriteMessage(MsgCode code, ReadOnlySpan<byte> payload) => WriteFrame(code, payload);

    private void WriteFrame(MsgCode code, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0xFFFFFF)
            throw new InvalidDataException("multiplex frame payload too large (>16MB)");

        Span<byte> header = stackalloc byte[4];
        uint word = (uint)((Mplex.Base + (int)code) << 24) | (uint)payload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(header, word);
        inner.Write(header);
        if (payload.Length > 0)
            inner.Write(payload);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Read side of the multiplexed framing. <see cref="Read"/> transparently yields only
/// MSG_DATA payload bytes; any other message code is dispatched to <paramref name="onMessage"/>
/// as it's encountered (INFO/WARNING/ERROR/STATS/... can arrive interleaved between data
/// frames at arbitrary points, per rsync's own read_a_msg()).
/// </summary>
public sealed class MultiplexReadStream(Stream inner, Action<MsgCode, byte[]>? onMessage = null) : Stream
{
    private byte[] _pending = [];
    private int _pendingPos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        while (_pendingPos >= _pending.Length)
        {
            if (!FillNextDataFrame())
                return 0; // clean EOF
        }

        int n = Math.Min(buffer.Length, _pending.Length - _pendingPos);
        _pending.AsSpan(_pendingPos, n).CopyTo(buffer);
        _pendingPos += n;
        return n;
    }

    /// <summary>Reads and dispatches frames until a non-empty MSG_DATA frame arrives, or EOF.</summary>
    private bool FillNextDataFrame()
    {
        Span<byte> header = stackalloc byte[4];
        while (true)
        {
            if (!ReadFullyOrZero(inner, header))
                return false;

            uint word = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int len = (int)(word & 0xFFFFFF);
            var code = (MsgCode)((int)(word >> 24) - Mplex.Base);

            byte[] payload = len > 0 ? new byte[len] : [];
            if (len > 0)
                inner.ReadExactly(payload);

            if (code == MsgCode.Data)
            {
                if (len == 0)
                    continue; // no bytes carried; keep looking for real data
                _pending = payload;
                _pendingPos = 0;
                return true;
            }

            onMessage?.Invoke(code, payload);
        }
    }

    /// <summary>Like Stream.ReadExactly, but returns false instead of throwing when EOF occurs
    /// exactly on a frame boundary (0 bytes available) — that's a clean disconnect, not an error.</summary>
    private static bool ReadFullyOrZero(Stream s, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0)
            {
                if (total == 0)
                    return false;
                throw new EndOfStreamException("Stream ended mid multiplex-frame-header");
            }
            total += n;
        }
        return true;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
