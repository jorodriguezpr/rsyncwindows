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

public class LongPathSupportTests
{
    [Fact]
    public void LocalAbsolutePath_GetsPrefixed()
    {
        string result = LongPathSupport.Ensure(@"C:\some\folder\file.txt");
        Assert.StartsWith(@"\\?\C:\", result);
        Assert.EndsWith(@"\some\folder\file.txt", result);
    }

    [Fact]
    public void AlreadyPrefixedPath_StaysPrefixed_NotDoublePrefixed()
    {
        string result = LongPathSupport.Ensure(@"\\?\C:\some\folder\file.txt");
        Assert.Equal(@"\\?\C:\some\folder\file.txt", result);
        Assert.DoesNotContain(@"\\?\\\?\", result);
    }

    [Fact]
    public void RelativePath_IsFullyResolvedThenPrefixed()
    {
        string result = LongPathSupport.Ensure("relative.txt");
        Assert.StartsWith(@"\\?\", result);
        Assert.EndsWith(@"\relative.txt", result);
        // fully resolved -- no leftover relative segments
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void UncPath_UsesUncPrefixForm()
    {
        string result = LongPathSupport.Ensure(@"\\server\share\file.txt");
        Assert.StartsWith(@"\\?\UNC\server\share\", result);
    }

    [Fact]
    public void DeeplyNestedPath_OverTraditionalMaxPath_CanActuallyBeWrittenAndRead()
    {
        // The real point of this class: prove a path well past the traditional 260-char
        // MAX_PATH limit round-trips through actual file I/O without throwing, which is exactly
        // what a plain (unprefixed) path would risk on a machine without the long-paths policy
        // enabled -- this test doesn't assume that policy either way, since \\?\ bypasses it.
        string root = Path.Combine(Path.GetTempPath(), "rsyncwin-longpath-" + Guid.NewGuid().ToString("N"));
        string deep = LongPathSupport.Ensure(root);
        try
        {
            // Build a path comfortably past 260 chars using nested segments.
            string segment = new string('a', 40);
            for (int i = 0; i < 8; i++)
                deep = Path.Combine(deep, segment);
            Assert.True(deep.Length > 260, $"test setup didn't actually exceed MAX_PATH (was {deep.Length} chars)");

            Directory.CreateDirectory(deep);
            string filePath = LongPathSupport.Ensure(Path.Combine(deep, "file.txt"));
            File.WriteAllBytes(filePath, "hello"u8.ToArray());

            Assert.True(File.Exists(filePath));
            Assert.Equal("hello"u8.ToArray(), File.ReadAllBytes(filePath));
        }
        finally
        {
            if (Directory.Exists(LongPathSupport.Ensure(root)))
                Directory.Delete(LongPathSupport.Ensure(root), recursive: true);
        }
    }
}
