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

using RsyncWindows.Core.Daemon;
using Xunit;

namespace RsyncWindows.Core.Tests.Daemon;

public class RsyncdSecretsFileTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-secrets-missing-" + Guid.NewGuid().ToString("N") + ".secrets");
        Assert.Empty(RsyncdSecretsFile.Load(path));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsUsersAndPasswords()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-secrets-roundtrip-" + Guid.NewGuid().ToString("N") + ".secrets");
        try
        {
            var entries = new List<RsyncdSecretsFile.Entry>
            {
                new("alice", "hunter2"),
                new("bob", "correct horse battery staple"),
            };

            RsyncdSecretsFile.Save(path, entries);
            var loaded = RsyncdSecretsFile.Load(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("alice", loaded[0].User);
            Assert.Equal("hunter2", loaded[0].Password);
            Assert.Equal("bob", loaded[1].User);
            Assert.Equal("correct horse battery staple", loaded[1].Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_IgnoresBlankAndCommentLines()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-secrets-comments-" + Guid.NewGuid().ToString("N") + ".secrets");
        try
        {
            File.WriteAllText(path, "# a comment\n\nalice:secret1\n# another comment\nbob:secret2\n");
            var loaded = RsyncdSecretsFile.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("alice", loaded[0].User);
            Assert.Equal("bob", loaded[1].User);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_EmptyEntries_WritesEmptyFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "rsyncwin-secrets-empty-" + Guid.NewGuid().ToString("N") + ".secrets");
        try
        {
            RsyncdSecretsFile.Save(path, []);
            Assert.Empty(RsyncdSecretsFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
