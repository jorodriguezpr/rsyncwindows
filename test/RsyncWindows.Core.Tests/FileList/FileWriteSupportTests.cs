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

public class FileWriteSupportTests
{
    [Fact]
    public void TryCreateDirectory_ValidPath_Succeeds()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-fws-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(FileWriteSupport.TryCreateDirectory(path, out string? error));
            Assert.Null(error);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void TryCreateDirectory_NameContainsColon_FailsGracefully()
    {
        // A colon mid-path-component is legal on Linux/ext4 but illegal on NTFS (reserved for
        // Alternate Data Streams) -- exactly the real-world case this class exists for (a
        // directory literally named "C:" inside a pushed source tree). The offending segment
        // must be embedded in an already-joined relative string (matching how
        // DaemonConnectionHandler.ResolvePath actually builds it: `Path.Combine(moduleRoot,
        // relativePath.Replace('/', '\\'))`) -- passing "C:" as its OWN Path.Combine argument
        // hits a different .NET special case (a lone drive-letter-shaped segment is treated as
        // ROOTED and silently discards everything before it, which would misrepresent this test).
        string baseDir = Path.Combine(Path.GetTempPath(), "rsyncwin-fws-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(baseDir, "sub\\C:");
        bool ok = FileWriteSupport.TryCreateDirectory(path, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryWriteFile_ValidPath_Succeeds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rsyncwin-fws-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "file.txt");
        try
        {
            byte[] data = "hello"u8.ToArray();
            Assert.True(FileWriteSupport.TryWriteFile(path, data, out string? error));
            Assert.Null(error);
            Assert.Equal(data, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryWriteFile_IllegalPathComponent_FailsGracefully_DoesNotThrow()
    {
        // See TryCreateDirectory_NameContainsColon_FailsGracefully's comment on why the illegal
        // segment must be embedded in one already-joined relative string, not its own Path.Combine argument.
        string dir = Path.Combine(Path.GetTempPath(), "rsyncwin-fws-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "C:\\nested.txt");
        bool ok = FileWriteSupport.TryWriteFile(path, "x"u8.ToArray(), out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
