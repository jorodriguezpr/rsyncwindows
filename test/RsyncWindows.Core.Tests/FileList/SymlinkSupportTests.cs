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

/// <summary>
/// Environment-dependent by nature (symlink creation needs SeCreateSymbolicLinkPrivilege, which
/// this test machine may or may not have) -- these tests assert on WHATEVER
/// <see cref="SymlinkSupport.TryCreateSymlink"/> actually reports rather than assuming either
/// outcome, so they're meaningful (and never flaky) on a privileged CI box, a Developer-Mode
/// dev machine, or a locked-down one alike: success must mean a real, correctly-targeted
/// symlink now exists; failure must mean no exception escaped and a non-null reason was given.
/// </summary>
public class SymlinkSupportTests
{
    [Fact]
    public void CanCreateSymlinks_DoesNotThrow_AndIsStableAcrossCalls()
    {
        bool first = SymlinkSupport.CanCreateSymlinks();
        bool second = SymlinkSupport.CanCreateSymlinks();
        Assert.Equal(first, second);
    }

    [Fact]
    public void TryCreateSymlink_EitherSucceedsWithARealCorrectSymlink_OrFailsWithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rsyncwin-symlink-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string targetFile = Path.Combine(dir, "target.txt");
        File.WriteAllText(targetFile, "hello");
        string linkPath = Path.Combine(dir, "link.txt");

        try
        {
            bool ok = SymlinkSupport.TryCreateSymlink(linkPath, "target.txt", out string? error);

            if (ok)
            {
                Assert.Null(error);
                var info = new FileInfo(linkPath);
                Assert.True(info.Exists);
                Assert.NotNull(info.LinkTarget);
                // Reading through the link must reach the real content -- proves it's a genuine,
                // correctly-targeted symlink, not just a file that happens to exist.
                Assert.Equal("hello", File.ReadAllText(linkPath));
            }
            else
            {
                Assert.NotNull(error);
                Assert.False(File.Exists(linkPath));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCreateSymlink_ConvertsWireSlashesToLocalSeparator()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rsyncwin-symlink-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "real.txt"), "nested");
        string linkPath = Path.Combine(dir, "link.txt");

        try
        {
            bool ok = SymlinkSupport.TryCreateSymlink(linkPath, "sub/real.txt", out string? error);
            if (ok)
            {
                var info = new FileInfo(linkPath);
                Assert.NotNull(info.LinkTarget);
                Assert.DoesNotContain('/', info.LinkTarget!);
                Assert.Equal("nested", File.ReadAllText(linkPath));
            }
            else
            {
                Assert.NotNull(error);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
