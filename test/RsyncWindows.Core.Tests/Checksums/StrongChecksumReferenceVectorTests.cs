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

using System.Text;
using RsyncWindows.Core.Checksums;
using Xunit;

namespace RsyncWindows.Core.Tests.Checksums;

/// <summary>
/// Reference vectors captured from a standalone compile+run of rsync 3.5.0's ACTUAL
/// lib/md5.c (verbatim function bodies, only the rsync.h dependency chain replaced with
/// minimal local shims -- see scratchpad/md5_vector.c) driving get_checksum2()'s CSUM_MD5
/// seed-framing logic (checksum.c:356-374) directly, compiled and run under WSL/gcc:
///
///   seed=0     data="hello world"        -> 5eb63bbbe01eeed093cb22bb8f5acdc3
///   seed=12345 data="hello world"        -> d1006ef1d7ccd7ac594e780694f39b88
///   seed=0x12345678 data=700-byte-pattern -> b44ce33ab5c643de191e3169d1d1592c
///   seed=42    data=""                   -> 9824a7030ce67cf3f0efe7529f0c6ecc
///   seed=-1    data="hello world"        -> cb19699f5df1c8c7fdc0ef5ea575e5c3
///
/// Vector 1 (seed=0) equals the universally-known standard MD5("hello world") hash, which
/// independently confirms the extraction harness itself is faithful before trusting the
/// seeded vectors. This is the test the plan's Phase 3 gate calls out explicitly: without it,
/// a seed-ordering bug would silently degrade every transfer to all-literal data while still
/// "succeeding" (see StrongChecksum's class doc comment) -- exactly the failure mode that
/// looks fine in casual testing and isn't caught by round-trip tests alone (both sides would
/// consistently agree on the same wrong value).
/// </summary>
public class StrongChecksumReferenceVectorTests
{
    [Fact]
    public void UnseededVector_MatchesStandardMd5()
    {
        var data = Encoding.ASCII.GetBytes("hello world");
        var actual = StrongChecksum.ComputeBlockChecksum(0, data);
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void SeededVector_PositiveSeed_MatchesRsyncsOwnMd5()
    {
        var data = Encoding.ASCII.GetBytes("hello world");
        var actual = StrongChecksum.ComputeBlockChecksum(12345, data);
        Assert.Equal("d1006ef1d7ccd7ac594e780694f39b88", Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void SeededVector_700ByteBlock_MatchesRsyncsOwnMd5()
    {
        var block = new byte[700];
        for (int i = 0; i < 700; i++)
            block[i] = unchecked((byte)(i * 7 + 3));

        var actual = StrongChecksum.ComputeBlockChecksum(0x12345678, block);
        Assert.Equal("b44ce33ab5c643de191e3169d1d1592c", Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void SeededVector_EmptyData_MatchesRsyncsOwnMd5()
    {
        var actual = StrongChecksum.ComputeBlockChecksum(42, []);
        Assert.Equal("9824a7030ce67cf3f0efe7529f0c6ecc", Convert.ToHexStringLower(actual));
    }

    [Fact]
    public void SeededVector_NegativeSeed_MatchesRsyncsOwnMd5()
    {
        // checksum_seed is a signed C int; -1's 4-byte little-endian representation
        // (0xFFFFFFFF) must be exactly what gets prepended.
        var data = Encoding.ASCII.GetBytes("hello world");
        var actual = StrongChecksum.ComputeBlockChecksum(-1, data);
        Assert.Equal("cb19699f5df1c8c7fdc0ef5ea575e5c3", Convert.ToHexStringLower(actual));
    }
}
