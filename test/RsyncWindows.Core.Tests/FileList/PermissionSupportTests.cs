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

public class PermissionSupportTests
{
    private const int OwnerWriteBit = 0x80; // S_IWUSR, octal 0200

    [Fact]
    public void ModeWithoutOwnerWrite_SetsReadOnlyAttribute()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-perm-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        try
        {
            int modeNoWrite = UnixMode.RegularFileDefault & ~OwnerWriteBit; // e.g. 0444
            PermissionSupport.TryApplyReadOnly(path, modeNoWrite);

            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void ModeWithOwnerWrite_ClearsReadOnlyAttribute()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-perm-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0); // sanity check

            PermissionSupport.TryApplyReadOnly(path, UnixMode.RegularFileDefault); // 0644 -- owner write set

            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-perm-does-not-exist-" + Guid.NewGuid().ToString("N") + ".txt");
        PermissionSupport.TryApplyReadOnly(path, UnixMode.RegularFileDefault);
    }
}
