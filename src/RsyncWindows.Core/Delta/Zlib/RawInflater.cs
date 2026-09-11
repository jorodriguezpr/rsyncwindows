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

namespace RsyncWindows.Core.Delta.Zlib;

/// <summary>
/// A minimal, real-zlib-backed raw-deflate decompressor exposing the same small surface
/// <see cref="CompressedTokenReader"/> needs (<see cref="SetInput"/>/<see cref="Inflate"/>/
/// <see cref="IsNeedingInput"/>) -- the counterpart to <see cref="RawDeflater"/>, see that
/// class's doc comment for why this project moved off SharpZipLib for the compressed token
/// stream.
/// </summary>
internal sealed unsafe class RawInflater : IDisposable
{
    private const int WindowBits = -15; // negative = raw deflate, no zlib header/trailer

    private ZlibNative.ZStream _stream;
    private byte[]? _pendingInput;
    private int _pendingOffset;
    private int _pendingRemaining;
    private bool _disposed;

    public RawInflater()
    {
        fixed (ZlibNative.ZStream* s = &_stream)
        {
            int rc = ZlibNative.CompressionNative_InflateInit2_(s, WindowBits);
            if (rc != (int)ZResult.Ok)
                throw new InvalidOperationException($"inflateInit2 failed: zlib rc={rc}");
        }
    }

    public bool IsNeedingInput => _pendingRemaining == 0;

    public void SetInput(byte[] input)
    {
        _pendingInput = input;
        _pendingOffset = 0;
        _pendingRemaining = input.Length;
    }

    public int Inflate(byte[] output)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RawInflater));

        byte[] input = _pendingInput ?? [];

        fixed (ZlibNative.ZStream* s = &_stream)
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            s->NextIn = inPtr + _pendingOffset;
            s->AvailIn = (uint)_pendingRemaining;
            s->NextOut = outPtr;
            s->AvailOut = (uint)output.Length;

            int rc = ZlibNative.CompressionNative_Inflate(s, (int)ZFlush.NoFlush);
            if (rc != (int)ZResult.Ok && rc != (int)ZResult.StreamEnd && rc != (int)ZResult.BufError)
                throw new InvalidDataException($"inflate failed: zlib rc={rc}");

            int consumed = _pendingRemaining - (int)s->AvailIn;
            _pendingOffset += consumed;
            _pendingRemaining -= consumed;

            return output.Length - (int)s->AvailOut;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        fixed (ZlibNative.ZStream* s = &_stream)
            ZlibNative.CompressionNative_InflateEnd(s);
    }
}
