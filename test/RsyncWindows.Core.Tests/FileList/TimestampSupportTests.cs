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

using RsyncWindows.Core.FileList;
using Xunit;

namespace RsyncWindows.Core.Tests.FileList;

/// <summary>Exercises the real NTFS filesystem (a temp file), not just DateTime math -- proves
/// there's no drift/rounding mismatch between what <see cref="TimestampSupport.TrySetModTimeUtc"/>
/// writes and what a later read-back (matching <see cref="FileListBuilder"/>'s own
/// <see cref="TimestampSupport.ToUnixSeconds"/> conversion) reports, for exactly the risk the
/// plan flagged: "NTFS 100ns ticks vs. rsync's second-or-finer granularity can cause spurious
/// 'changed' detection on repeat syncs if truncation isn't applied consistently both sides."</summary>
public class TimestampSupportTests
{
    [Theory]
    [InlineData(0L)]              // Unix epoch
    [InlineData(1_700_000_000L)]  // a realistic recent timestamp
    [InlineData(1L)]              // just past epoch
    public void SetThenReadBack_RoundTripsExactly_NoDrift(long unixSeconds)
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-ts-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        try
        {
            TimestampSupport.TrySetModTimeUtc(path, unixSeconds);

            DateTime writtenUtc = File.GetLastWriteTimeUtc(path);
            long readBack = TimestampSupport.ToUnixSeconds(writtenUtc);

            Assert.Equal(unixSeconds, readBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RepeatedSetWithSameValue_StaysStable_NoCumulativeDrift()
    {
        // Guards against a subtly different bug: repeatedly re-applying the SAME logical
        // timestamp (e.g. re-syncing a file that hasn't actually changed) must not accumulate
        // rounding error across calls.
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-ts-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        try
        {
            const long value = 1_650_123_456L;
            for (int i = 0; i < 5; i++)
                TimestampSupport.TrySetModTimeUtc(path, value);

            long readBack = TimestampSupport.ToUnixSeconds(File.GetLastWriteTimeUtc(path));
            Assert.Equal(value, readBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-ts-does-not-exist-" + Guid.NewGuid().ToString("N") + ".txt");
        TimestampSupport.TrySetModTimeUtc(path, 12345);
    }
}
