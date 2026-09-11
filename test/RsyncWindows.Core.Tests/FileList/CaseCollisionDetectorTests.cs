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

public class CaseCollisionDetectorTests
{
    private static FileEntry Entry(string path) => new()
    {
        Path = path,
        FileType = RsyncFileType.Regular,
        Mode = UnixMode.RegularFileDefault,
        ModTimeUnix = 0,
    };

    [Fact]
    public void DistinctPaths_NoCollision_DoesNotThrow()
    {
        var entries = new[] { Entry("a.txt"), Entry("b.txt"), Entry("dir/a.txt") };
        CaseCollisionDetector.ThrowIfCollisions(entries);
    }

    [Fact]
    public void SameCaseDuplicate_IsNotTreatedAsACollision()
    {
        // Two entries with the IDENTICAL path (same case) isn't the case-insensitivity problem
        // this class exists for -- exercised here to prove the detector groups by exact string
        // too, not just case-insensitive bucket membership.
        var entries = new[] { Entry("a.txt"), Entry("a.txt") };
        CaseCollisionDetector.ThrowIfCollisions(entries);
    }

    [Fact]
    public void TwoPathsDifferingOnlyByCase_Throws()
    {
        var entries = new[] { Entry("File.txt"), Entry("file.txt") };
        var ex = Assert.Throws<InvalidOperationException>(() => CaseCollisionDetector.ThrowIfCollisions(entries));
        Assert.Contains("File.txt", ex.Message);
        Assert.Contains("file.txt", ex.Message);
    }

    [Fact]
    public void CaseDifferenceInsideADirectoryComponent_Throws()
    {
        var entries = new[] { Entry("Docs/readme.txt"), Entry("docs/readme.txt") };
        Assert.Throws<InvalidOperationException>(() => CaseCollisionDetector.ThrowIfCollisions(entries));
    }

    [Fact]
    public void DifferentDirectories_SameLeafCaseVariant_DoesNotCollide()
    {
        // "a/File.txt" and "b/file.txt" are NOT the same destination path -- only an exact
        // (case-insensitive) full-path match is a real collision.
        var entries = new[] { Entry("a/File.txt"), Entry("b/file.txt") };
        CaseCollisionDetector.ThrowIfCollisions(entries);
    }

    [Fact]
    public void ThreeWayCollision_ReportsAllVariants()
    {
        var entries = new[] { Entry("x.txt"), Entry("X.txt"), Entry("X.TXT") };
        var ex = Assert.Throws<InvalidOperationException>(() => CaseCollisionDetector.ThrowIfCollisions(entries));
        Assert.Contains("x.txt", ex.Message);
        Assert.Contains("X.txt", ex.Message);
        Assert.Contains("X.TXT", ex.Message);
    }
}
