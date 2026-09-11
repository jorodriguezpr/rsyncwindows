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

public class ServerInvocationBuilderTests
{
    /// <summary>
    /// Regression test for a real bug: an empty remote path (produced by
    /// <see cref="RemoteSpec.Parse"/> for the extremely common "push/pull the module's own
    /// root" case -- `rsync://host/module/`, `rsync://host/module` with no trailing slash, or
    /// `host::module`/`host::module/`) used to be passed straight through as a literal empty
    /// string, the LAST argument in the NUL-terminated argv list
    /// <see cref="Daemon.DaemonHandshake.RunClientSide"/> sends. An empty string is
    /// indistinguishable from that list's own terminator, so the argument silently vanished and
    /// the real terminator's stray NUL byte got misread as the start of whatever the peer read
    /// next (confirmed directly against a real daemon connection: it surfaced as "peer's
    /// checksum-choice list ('') does not include md5", nowhere near the actual bug). Real
    /// rsync represents "no subpath, just the root" as "." -- this must too.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullRemotePath_BecomesDot_NotALiteralEmptyString(string? remotePath)
    {
        var options = new RsyncOptions();
        var args = ServerInvocationBuilder.BuildServerArgs(options, amSender: true, remotePath!);

        Assert.DoesNotContain("", args);
        Assert.Equal(".", args[^1]);
    }

    [Fact]
    public void NonEmptyRemotePath_PassedThroughUnchanged()
    {
        var options = new RsyncOptions();
        var args = ServerInvocationBuilder.BuildServerArgs(options, amSender: true, "some/real/path");

        Assert.Equal("some/real/path", args[^1]);
    }

    [Fact]
    public void ArgsList_NeverContainsAnEmptyElement_RegardlessOfOptions()
    {
        // The NUL-terminated wire encoding treats ANY empty argv element as end-of-list, not
        // just the final path -- so this must hold generally, not just for the specific
        // empty-remote-path case above.
        var options = new RsyncOptions { Verbose = 2, Recursive = true, PreserveTimes = true };
        var args = ServerInvocationBuilder.BuildServerArgs(options, amSender: false, "");

        Assert.DoesNotContain(args, a => a.Length == 0);
    }
}
