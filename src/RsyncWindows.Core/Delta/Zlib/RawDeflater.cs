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
/// A minimal, real-zlib-backed raw-deflate compressor exposing the same small surface
/// <see cref="CompressedTokenWriter"/> needs (<see cref="SetInput"/>/<see cref="Deflate"/>/
/// <see cref="IsNeedingInput"/>/<see cref="Flush"/>) -- a drop-in replacement for SharpZipLib's
/// <c>Deflater</c>, which has a confirmed bug corrupting multi-flush sequences when a
/// sync-flushed block happens to be stored rather than Huffman-coded (see
/// <see cref="CompressedTokenWriter.PrimeFromMatchedBlock"/>'s doc comment for the full
/// writeup). Real zlib's own <c>Z_SYNC_FLUSH</c> does not have that bug -- it's the exact
/// mechanism real rsync's own C source relies on for this same technique.
/// </summary>
internal sealed unsafe class RawDeflater : IDisposable
{
    private const int WindowBits = -15; // negative = raw deflate, no zlib header/trailer (RFC1951 only)
    private const int Method = 8;       // Z_DEFLATED
    private const int MemLevel = 8;     // zlib's own default
    private const int DefaultCompression = -1;
    private const int DefaultStrategy = 0;

    private ZlibNative.ZStream _stream;
    private byte[]? _pendingInput;
    private int _pendingOffset;
    private int _pendingRemaining;
    private bool _disposed;

    public RawDeflater()
    {
        fixed (ZlibNative.ZStream* s = &_stream)
        {
            int rc = ZlibNative.CompressionNative_DeflateInit2_(s, DefaultCompression, Method, WindowBits, MemLevel, DefaultStrategy);
            if (rc != (int)ZResult.Ok)
                throw new InvalidOperationException($"deflateInit2 failed: zlib rc={rc}");
        }
    }

    public bool IsNeedingInput => _pendingRemaining == 0;

    public void SetInput(byte[] input)
    {
        _pendingInput = input;
        _pendingOffset = 0;
        _pendingRemaining = input.Length;
    }

    /// <summary>Requests a sync-flush point on the NEXT <see cref="Deflate"/> call (matching
    /// SharpZipLib's <c>Deflater.Flush()</c> API shape: it only sets a flag here, the actual
    /// flush happens via the following Deflate() call(s), draining until output stops).</summary>
    public void Flush() => _flushRequested = true;

    private bool _flushRequested;

    public int Deflate(byte[] output)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RawDeflater));

        int flush = _flushRequested ? (int)ZFlush.SyncFlush : (int)ZFlush.NoFlush;
        byte[] input = _pendingInput ?? [];

        fixed (ZlibNative.ZStream* s = &_stream)
        fixed (byte* inPtr = input)
        fixed (byte* outPtr = output)
        {
            s->NextIn = inPtr + _pendingOffset;
            s->AvailIn = (uint)_pendingRemaining;
            s->NextOut = outPtr;
            s->AvailOut = (uint)output.Length;

            int rc = ZlibNative.CompressionNative_Deflate(s, flush);
            if (rc != (int)ZResult.Ok && rc != (int)ZResult.StreamEnd && rc != (int)ZResult.BufError)
                throw new InvalidOperationException($"deflate failed: zlib rc={rc}");

            int consumed = _pendingRemaining - (int)s->AvailIn;
            _pendingOffset += consumed;
            _pendingRemaining -= consumed;

            int produced = output.Length - (int)s->AvailOut;

            // A sync-flush is only "done" once zlib stops producing output for it while there's
            // no more input to feed -- matching real deflate()'s own documented drain contract.
            // Once fully drained, clear the request so the NEXT literal's Flush() call starts a
            // fresh flush cycle rather than this one bleeding into it.
            if (_flushRequested && produced == 0 && _pendingRemaining == 0)
                _flushRequested = false;

            return produced;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        fixed (ZlibNative.ZStream* s = &_stream)
            ZlibNative.CompressionNative_DeflateEnd(s);
    }
}
