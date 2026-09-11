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

using RsyncWindows.Core.Options;
using Xunit;

namespace RsyncWindows.Core.Tests.Options;

public class RsyncArgParserTests
{
    [Fact]
    public void ArchiveFlag_ExpandsToIndividualAttrs()
    {
        var o = RsyncArgParser.Parse(["-a", "src/", "dest/"]);
        Assert.True(o.Recursive);
        Assert.True(o.PreserveLinks);
        Assert.True(o.PreservePerms);
        Assert.True(o.PreserveTimes);
        Assert.True(o.PreserveOwner);
        Assert.True(o.PreserveGroup);
        Assert.True(o.PreserveDevices);
        Assert.True(o.PreserveSpecials);
    }

    [Fact]
    public void BundledShortFlags_AllApply()
    {
        var o = RsyncArgParser.Parse(["-avz", "src/", "dest/"]);
        Assert.Equal(1, o.Verbose);
        Assert.True(o.Recursive); // from -a
        Assert.True(o.Compress);
    }

    [Fact]
    public void RepeatedVerbose_Accumulates()
    {
        var o = RsyncArgParser.Parse(["-vvv", "src/", "dest/"]);
        Assert.Equal(3, o.Verbose);
    }

    [Fact]
    public void ArchiveWithNegation_OverridesIndividualAttr()
    {
        var o = RsyncArgParser.Parse(["-a", "--no-owner", "src/", "dest/"]);
        Assert.True(o.Recursive);
        Assert.False(o.PreserveOwner);
    }

    [Fact]
    public void LongOptionWithEqualsValue_Parses()
    {
        var o = RsyncArgParser.Parse(["--exclude=*.log", "src/", "dest/"]);
        Assert.Equal(["*.log"], o.ExcludePatterns);
    }

    [Fact]
    public void LongOptionWithSeparateValue_Parses()
    {
        var o = RsyncArgParser.Parse(["--exclude", "*.log", "src/", "dest/"]);
        Assert.Equal(["*.log"], o.ExcludePatterns);
    }

    [Fact]
    public void ShortOptionWithAttachedValue_Parses()
    {
        // -e/bin/ssh -- the rest of the bundle after 'e' is the value.
        var o = RsyncArgParser.Parse(["-e/bin/ssh", "src/", "dest/"]);
        Assert.Equal("/bin/ssh", o.RemoteShell);
    }

    [Fact]
    public void ShortOptionWithSeparateValue_Parses()
    {
        var o = RsyncArgParser.Parse(["-e", "ssh -p 2222", "src/", "dest/"]);
        Assert.Equal("ssh -p 2222", o.RemoteShell);
    }

    [Fact]
    public void PFlag_ExpandsToPartialAndProgress()
    {
        var o = RsyncArgParser.Parse(["-P", "src/", "dest/"]);
        Assert.True(o.Partial);
        Assert.True(o.Progress);
    }

    [Fact]
    public void DeleteVariants_SetTimingAndImplyDelete()
    {
        Assert.Equal(DeleteTiming.Before, RsyncArgParser.Parse(["--delete-before", "s", "d"]).DeleteTiming);
        Assert.Equal(DeleteTiming.During, RsyncArgParser.Parse(["--delete-during", "s", "d"]).DeleteTiming);
        var during = RsyncArgParser.Parse(["--delete-during", "s", "d"]);
        Assert.True(during.Delete);
    }

    [Fact]
    public void MultipleSourcesAndDestination_Classified()
    {
        var o = RsyncArgParser.Parse(["-a", "src1/", "src2/", "dest/"]);
        Assert.Equal(["src1/", "src2/"], o.Sources);
        Assert.Equal("dest/", o.Destination);
    }

    [Fact]
    public void DoubleDashEndsOptionParsing()
    {
        var o = RsyncArgParser.Parse(["-a", "--", "-oddly-named-src", "dest/"]);
        Assert.Equal(["-oddly-named-src"], o.Sources);
    }

    [Fact]
    public void BwLimitSuffixes_ParseCorrectly()
    {
        Assert.Equal(500, RsyncArgParser.Parse(["--bwlimit=500", "s", "d"]).BwLimitKBytes);
        Assert.Equal(1024, RsyncArgParser.Parse(["--bwlimit=1m", "s", "d"]).BwLimitKBytes);
        Assert.Equal(2048 * 1024, RsyncArgParser.Parse(["--bwlimit=2g", "s", "d"]).BwLimitKBytes);
    }

    [Fact]
    public void UnknownLongOption_Throws()
    {
        Assert.Throws<RsyncArgException>(() => RsyncArgParser.Parse(["--not-a-real-option", "s", "d"]));
    }

    [Fact]
    public void UnknownShortOption_Throws()
    {
        Assert.Throws<RsyncArgException>(() => RsyncArgParser.Parse(["-Q", "s", "d"]));
    }

    [Fact]
    public void MissingDestination_Throws()
    {
        Assert.Throws<RsyncArgException>(() => RsyncArgParser.Parse(["-a", "src-only/"]));
    }
}
