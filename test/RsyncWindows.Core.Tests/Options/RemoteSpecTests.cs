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

public class RemoteSpecTests
{
    [Theory]
    [InlineData(@"C:\Users\jrpco\data")]
    [InlineData("C:/Users/jrpco/data")]
    [InlineData(@"D:\backup")]
    [InlineData("relative/path")]
    [InlineData(@"relative\path")]
    [InlineData("/already/absolute/unix/style")]
    public void WindowsAndPlainPaths_ClassifyAsLocal(string token)
    {
        var spec = RemoteSpec.Parse(token);
        var local = Assert.IsType<RemoteSpec.Local>(spec);
        Assert.Equal(token, local.Path);
    }

    [Fact]
    public void HostColonPath_ClassifiesAsSsh()
    {
        var spec = RemoteSpec.Parse("myhost:/var/www");
        var ssh = Assert.IsType<RemoteSpec.Ssh>(spec);
        Assert.Null(ssh.User);
        Assert.Equal("myhost", ssh.Host);
        Assert.Equal("/var/www", ssh.Path);
    }

    [Fact]
    public void UserAtHostColonPath_ClassifiesAsSshWithUser()
    {
        var spec = RemoteSpec.Parse("root@192.0.2.10:/usr/local/myapp");
        var ssh = Assert.IsType<RemoteSpec.Ssh>(spec);
        Assert.Equal("root", ssh.User);
        Assert.Equal("192.0.2.10", ssh.Host);
        Assert.Equal("/usr/local/myapp", ssh.Path);
    }

    [Fact]
    public void DoubleColonModuleSyntax_ClassifiesAsDaemon()
    {
        var spec = RemoteSpec.Parse("backuphost::mymodule/some/path");
        var daemon = Assert.IsType<RemoteSpec.Daemon>(spec);
        Assert.Equal("backuphost", daemon.Host);
        Assert.Equal("mymodule", daemon.Module);
        Assert.Equal("some/path", daemon.Path);
        Assert.Null(daemon.Port);
    }

    [Fact]
    public void RsyncUrlSyntax_ClassifiesAsDaemonWithPort()
    {
        var spec = RemoteSpec.Parse("rsync://user@host:8730/module/sub/dir");
        var daemon = Assert.IsType<RemoteSpec.Daemon>(spec);
        Assert.Equal("user", daemon.User);
        Assert.Equal("host", daemon.Host);
        Assert.Equal(8730, daemon.Port);
        Assert.Equal("module", daemon.Module);
        Assert.Equal("sub/dir", daemon.Path);
    }

    [Fact]
    public void RsyncUrlSyntax_NoPathAfterModule()
    {
        var spec = RemoteSpec.Parse("rsync://host/module");
        var daemon = Assert.IsType<RemoteSpec.Daemon>(spec);
        Assert.Equal("", daemon.Path);
    }

    [Fact]
    public void SingleLetterHost_WithActualDoubleColon_StillDaemon()
    {
        // Confirms the drive-letter special-case doesn't accidentally swallow a REAL
        // single-letter daemon hostname when it's unambiguous (double colon).
        var spec = RemoteSpec.Parse("x::module/path");
        var daemon = Assert.IsType<RemoteSpec.Daemon>(spec);
        Assert.Equal("x", daemon.Host);
    }
}
