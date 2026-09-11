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

using System.Runtime.InteropServices;

namespace RsyncWindows.Core.Delta.Zlib;

/// <summary>
/// P/Invoke surface for real zlib's raw-deflate stream API, via
/// <c>System.IO.Compression.Native</c> -- the small, genuinely-zlib-backed native library every
/// .NET runtime install ships alongside itself (used internally by .NET's own
/// <see cref="System.IO.Compression.DeflateStream"/>), NOT a separate bundled dependency. Its
/// exported <c>CompressionNative_*</c> functions (declared in the dotnet/runtime source at
/// <c>src/native/libs/System.IO.Compression.Native/pal_zlib.h</c>/<c>.c</c>) are a thin,
/// undocumented-but-stable passthrough directly to zlib's own <c>deflate()</c>/<c>inflate()</c> --
/// confirmed by reading that C source: the `flush` parameter is forwarded to zlib's `deflate`/
/// `inflate` with NO validation against the PAL's own (deliberately narrower) `PAL_FlushCode`
/// enum, meaning <see cref="ZFlush.SyncFlush"/> (real zlib's `Z_SYNC_FLUSH`=2, not one of the two
/// values the PAL header itself names) works correctly even though it isn't a "documented" PAL
/// value -- this is exactly the manual sync-flush control this project's `-z` support needs and
/// neither SharpZipLib's raw <c>Deflater</c> (see the bug writeup on
/// <see cref="CompressedTokenWriter.PrimeFromMatchedBlock"/>) nor .NET's own public
/// <see cref="System.IO.Compression.DeflateStream"/> API exposes.
///
/// Struct layout and function signatures are transcribed byte-for-byte from `pal_zlib.h`'s
/// `PAL_ZStream` and `CompressionNative_*` declarations -- this is the actual native ABI
/// (confirmed present at `%ProgramFiles%\dotnet\shared\Microsoft.NETCore.App\&lt;version&gt;\
/// System.IO.Compression.Native.dll` on this machine), not a guess.
/// </summary>
internal static unsafe partial class ZlibNative
{
    private const string LibraryName = "System.IO.Compression.Native";

    /// <summary>Matches <c>PAL_ZStream</c> field-for-field and in order: two data pointers, an
    /// error-message pointer zlib may set, an opaque handle to the real <c>z_stream</c> the
    /// native side owns (must be left untouched by managed code between calls), then the two
    /// available-byte-count fields.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ZStream
    {
        public byte* NextIn;
        public byte* NextOut;
        public byte* Msg;
        public void* InternalState;
        public uint AvailIn;
        public uint AvailOut;
    }

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_DeflateInit2_(ZStream* stream, int level, int method, int windowBits, int memLevel, int strategy);

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_Deflate(ZStream* stream, int flush);

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_DeflateEnd(ZStream* stream);

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_InflateInit2_(ZStream* stream, int windowBits);

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_Inflate(ZStream* stream, int flush);

    [LibraryImport(LibraryName)]
    internal static partial int CompressionNative_InflateEnd(ZStream* stream);
}

/// <summary>zlib flush codes (zlib.h) -- only <see cref="NoFlush"/> and <see cref="Finish"/> are
/// named in the PAL header's own <c>PAL_FlushCode</c> enum, but <see cref="SyncFlush"/>'s value
/// (2) passes straight through uninspected, per <see cref="ZlibNative"/>'s doc comment.</summary>
internal enum ZFlush
{
    NoFlush = 0,
    SyncFlush = 2,
    Finish = 4,
}

/// <summary>zlib result codes (zlib.h) actually reachable from raw-deflate usage here.</summary>
internal enum ZResult
{
    Ok = 0,
    StreamEnd = 1,
    StreamError = -2,
    DataError = -3,
    MemError = -4,
    BufError = -5,
    VersionError = -6,
}
