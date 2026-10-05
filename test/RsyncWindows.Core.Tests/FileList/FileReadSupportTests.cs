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

using System.Security.AccessControl;
using System.Security.Principal;
using RsyncWindows.Core.FileList;
using Xunit;

namespace RsyncWindows.Core.Tests.FileList;

public class FileReadSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rsyncwin-frs-" + Guid.NewGuid().ToString("N"));

    public FileReadSupportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir))
        {
            RemoveDenyRules(f);
            File.SetAttributes(f, FileAttributes.Normal);
        }
        Directory.Delete(_dir, recursive: true);
    }

    private string NewFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TryReadFile_ReadableFile_ReturnsContent()
    {
        string path = NewFile("ok.txt", "hello");
        Assert.True(FileReadSupport.TryReadFile(path, out byte[] data, out string? error));
        Assert.Null(error);
        Assert.Equal("hello"u8.ToArray(), data);
    }

    [Fact]
    public void TryReadFile_FileOpenForWritingByAnotherProgram_StillReadable()
    {
        // An editor / IIS / PHP process holding the file open for writing (sharing reads) -- plain
        // File.ReadAllBytes fails here with "being used by another process".
        string path = NewFile("open.txt", "in use");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => File.ReadAllBytes(path));
        Assert.True(FileReadSupport.TryReadFile(path, out byte[] data, out _));
        Assert.Equal("in use"u8.ToArray(), data);
    }

    [Fact]
    public void ReadBasisOrEmpty_MissingFile_EmptyWithoutError()
    {
        byte[] basis = FileReadSupport.ReadBasisOrEmpty(Path.Combine(_dir, "nope.txt"), out string? error);
        Assert.Empty(basis);
        Assert.Null(error);
    }

    [Fact]
    public void ReadBasisOrEmpty_ExclusivelyLockedFile_EmptyWithError()
    {
        string path = NewFile("locked.txt", "secret");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] basis = FileReadSupport.ReadBasisOrEmpty(path, out string? error);
        Assert.Empty(basis);
        Assert.NotNull(error);
    }

    [Fact]
    public void ReadBasisOrEmpty_AccessDenied_EmptyWithError()
    {
        // The reported case: "Access to the path '\\?\I:\...\config.php' is denied."
        if (!OperatingSystem.IsWindows())
            return;
        string path = LongPathSupport.Ensure(NewFile("config.php", "<?php // db password"));
        DenyCurrentUser(path, FileSystemRights.ReadData);
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));

        byte[] basis = FileReadSupport.ReadBasisOrEmpty(path, out string? error);
        Assert.Empty(basis);
        Assert.Contains("denied", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryWriteFile_ReadOnlyTarget_IsReplaced()
    {
        string path = NewFile("ro.txt", "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.True(FileWriteSupport.TryWriteFile(path, "new"u8.ToArray(), out string? error), error);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void TryWriteFile_DeniedTarget_FailsGracefullyAndKeepsReadOnly()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string path = NewFile("deny-ro.txt", "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        DenyCurrentUser(path, FileSystemRights.WriteData);
        Assert.False(FileWriteSupport.TryWriteFile(path, "new"u8.ToArray(), out string? error));
        Assert.NotNull(error);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly), "read-only must be restored after a failed write");
    }

    [Fact]
    public void TryCopyFile_OverReadOnlyTarget_Succeeds()
    {
        string src = NewFile("src.txt", "fresh");
        string dst = NewFile("dst.txt", "stale");
        File.SetAttributes(dst, FileAttributes.ReadOnly);
        Assert.True(FileWriteSupport.TryCopyFile(src, dst, out string? error), error);
        Assert.Equal("fresh", File.ReadAllText(dst));
    }

    [Fact]
    public void TryCopyFile_LockedSource_FailsGracefully()
    {
        string src = NewFile("src-locked.txt", "x");
        string dst = Path.Combine(_dir, "dst-new.txt");
        using var exclusive = new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(FileWriteSupport.TryCopyFile(src, dst, out string? error));
        Assert.NotNull(error);
    }

    private static void DenyCurrentUser(string path, FileSystemRights rights)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var info = new FileInfo(path);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny));
        info.SetAccessControl(acl);
    }

    private static void RemoveDenyRules(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Deny)
                    acl.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(acl);
        }
        catch { /* best-effort cleanup */ }
    }
}
