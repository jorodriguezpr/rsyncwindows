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

using System.Net;
using RsyncWindows.Core.Daemon;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

public class RsyncdConfigTests
{
    [Fact]
    public void Parse_ReadsModuleFields()
    {
        var config = RsyncdConfig.Parse(
        [
            "# a comment line",
            "[backup]",
            "  path = D:\\Backups  ",
            "comment = Nightly backups",
            "read only = no",
            "list = no",
            "hosts allow = 192.168.1.5, 10.0.0.0/8",
            "auth users = alice, bob",
            "secrets file = D:\\rsyncd.secrets",
            "max connections = 4",
            "timeout = 300",
        ]);

        var m = Assert.Single(config.Modules);
        Assert.Equal("backup", m.Name);
        Assert.Equal("D:\\Backups", m.Path);
        Assert.Equal("Nightly backups", m.Comment);
        Assert.False(m.ReadOnly);
        Assert.False(m.List);
        Assert.Equal(["192.168.1.5", "10.0.0.0/8"], m.HostsAllow);
        Assert.Equal(["alice", "bob"], m.AuthUsers);
        Assert.Equal("D:\\rsyncd.secrets", m.SecretsFile);
        Assert.Equal(4, m.MaxConnections);
        Assert.Equal(300, m.TimeoutSeconds);
        Assert.True(m.RequiresAuth);
    }

    [Fact]
    public void Parse_DefaultsReadOnlyAndListToTrue()
    {
        var config = RsyncdConfig.Parse(["[plain]", "path = C:\\x"]);
        var m = Assert.Single(config.Modules);
        Assert.True(m.ReadOnly);
        Assert.True(m.List);
        Assert.False(m.RequiresAuth);
    }

    [Fact]
    public void Parse_MultipleModules()
    {
        var config = RsyncdConfig.Parse(
        [
            "[one]",
            "path = C:\\one",
            "[two]",
            "path = C:\\two",
        ]);
        Assert.Equal(2, config.Modules.Count);
        Assert.NotNull(config.FindModule("one"));
        Assert.NotNull(config.FindModule("TWO")); // case-insensitive lookup
        Assert.Null(config.FindModule("three"));
    }

    [Theory]
    [InlineData("*", "1.2.3.4", true)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.6", false)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    public void IsHostAllowed_AllowListMatching(string rule, string address, bool expected)
    {
        var config = RsyncdConfig.Parse(["[m]", "path = C:\\x", $"hosts allow = {rule}"]);
        var m = config.Modules[0];
        Assert.Equal(expected, RsyncdConfig.IsHostAllowed(m, IPAddress.Parse(address)));
    }

    [Fact]
    public void IsHostAllowed_DenyListOnly_BlocksMatchingAddress()
    {
        var config = RsyncdConfig.Parse(["[m]", "path = C:\\x", "hosts deny = 10.0.0.0/8"]);
        var m = config.Modules[0];
        Assert.False(RsyncdConfig.IsHostAllowed(m, IPAddress.Parse("10.1.2.3")));
        Assert.True(RsyncdConfig.IsHostAllowed(m, IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void IsHostAllowed_NeitherListSet_AllowsEverything()
    {
        var config = RsyncdConfig.Parse(["[m]", "path = C:\\x"]);
        var m = config.Modules[0];
        Assert.True(RsyncdConfig.IsHostAllowed(m, IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllModuleFields()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-config-roundtrip-" + Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            var modules = new List<RsyncdModule>
            {
                new()
                {
                    Name = "backup",
                    Path = @"D:\Backups",
                    Comment = "Nightly backups",
                    ReadOnly = false,
                    List = false,
                    HostsAllow = ["192.168.1.5", "10.0.0.0/8"],
                    HostsDeny = ["203.0.113.0/24"],
                    AuthUsers = ["alice", "bob"],
                    SecretsFile = @"D:\rsyncd.secrets",
                    MaxConnections = 4,
                    TimeoutSeconds = 300,
                },
                new() { Name = "public", Path = @"C:\Shares\public" },
            };

            RsyncdConfig.Save(path, port: 8730, modules);
            var loaded = RsyncdConfig.Load(path);

            Assert.Equal(8730, loaded.Port);
            Assert.Equal(2, loaded.Modules.Count);

            var backup = loaded.FindModule("backup")!;
            Assert.Equal(@"D:\Backups", backup.Path);
            Assert.Equal("Nightly backups", backup.Comment);
            Assert.False(backup.ReadOnly);
            Assert.False(backup.List);
            Assert.Equal(["192.168.1.5", "10.0.0.0/8"], backup.HostsAllow);
            Assert.Equal(["203.0.113.0/24"], backup.HostsDeny);
            Assert.Equal(["alice", "bob"], backup.AuthUsers);
            Assert.Equal(@"D:\rsyncd.secrets", backup.SecretsFile);
            Assert.Equal(4, backup.MaxConnections);
            Assert.Equal(300, backup.TimeoutSeconds);

            var pub = loaded.FindModule("public")!;
            Assert.True(pub.ReadOnly); // default preserved for a module with no explicit setting
            Assert.True(pub.List);
            Assert.Empty(pub.AuthUsers);
            Assert.Null(pub.SecretsFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_DefaultPort_OmitsPortLine_LoadsBackAs873()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-config-defaultport-" + Guid.NewGuid().ToString("N") + ".conf");
        try
        {
            RsyncdConfig.Save(path, port: 873, [new RsyncdModule { Name = "m", Path = @"C:\x" }]);
            Assert.Equal(873, RsyncdConfig.Load(path).Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
