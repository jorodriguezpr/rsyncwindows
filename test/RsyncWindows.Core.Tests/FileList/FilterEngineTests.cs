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

public class FilterEngineTests
{
    [Fact]
    public void NoRules_IncludesEverything()
    {
        var f = new FilterEngine();
        Assert.True(f.IsIncluded("anything/at/all.txt"));
    }

    [Fact]
    public void SimpleWildcard_MatchesAtAnyDepth()
    {
        var f = new FilterEngine();
        f.AddExclude("*.log");
        Assert.False(f.IsIncluded("app.log"));
        Assert.False(f.IsIncluded("sub/dir/app.log"));
        Assert.True(f.IsIncluded("app.txt"));
    }

    [Fact]
    public void AnchoredPattern_OnlyMatchesAtRoot()
    {
        var f = new FilterEngine();
        f.AddExclude("/build");
        Assert.False(f.IsIncluded("build"));
        Assert.False(f.IsIncluded("build/output.txt")); // matching a dir also excludes its contents
        Assert.True(f.IsIncluded("sub/build"));
    }

    [Fact]
    public void DoubleStar_MatchesAcrossDirectorySeparators()
    {
        var f = new FilterEngine();
        f.AddExclude("**/node_modules/**");
        Assert.False(f.IsIncluded("a/b/node_modules/pkg/index.js"));
        Assert.True(f.IsIncluded("a/b/node_modules_backup/index.js"));
    }

    [Fact]
    public void IncludeRegisteredBeforeExclude_Wins()
    {
        var f = new FilterEngine();
        f.AddInclude("keep.log");
        f.AddExclude("*.log");
        Assert.True(f.IsIncluded("keep.log"));
        Assert.False(f.IsIncluded("other.log"));
    }

    [Fact]
    public void QuestionMark_MatchesExactlyOneCharacter()
    {
        var f = new FilterEngine();
        f.AddExclude("file?.txt");
        Assert.False(f.IsIncluded("file1.txt"));
        Assert.True(f.IsIncluded("file12.txt"));
        Assert.True(f.IsIncluded("file.txt"));
    }
}
