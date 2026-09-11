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

namespace RsyncWindows.Core.FileList;

/// <summary>
/// Windows symlink creation needs `SeCreateSymbolicLinkPrivilege` (granted by default only to
/// admins running elevated, or to anyone once Developer Mode is on) -- a real, common gap the
/// project's own plan calls out: "Detect at startup with a throwaway test symlink; degrade
/// gracefully (skip + warn) rather than failing the whole transfer." This class is that
/// detection + the actual creation call, kept out of <c>RsyncWindows.Core.Session</c> (which
/// never touches disk itself, per the project's transport-agnostic design rule) -- callers
/// (the CLI's receive paths, the daemon's) invoke this explicitly when writing a received
/// symlink entry to disk.
/// </summary>
public static class SymlinkSupport
{
    private static bool? _canCreateSymlinks;

    /// <summary>Cached after the first call (the privilege doesn't change mid-process). Probes
    /// with a real throwaway symlink rather than inspecting the process token's privilege list
    /// directly -- simpler, and it's the actual operation that matters, not the theoretical
    /// privilege state (e.g. Windows also allows this via Developer Mode through a different
    /// mechanism than the classical privilege grant).</summary>
    public static bool CanCreateSymlinks()
    {
        if (_canCreateSymlinks.HasValue)
            return _canCreateSymlinks.Value;

        string dir = Path.GetTempPath();
        string linkPath = Path.Combine(dir, "rsyncwin-symlink-probe-" + Guid.NewGuid().ToString("N"));
        string targetPath = linkPath + ".target";
        try
        {
            File.WriteAllBytes(targetPath, []);
            File.CreateSymbolicLink(linkPath, targetPath);
            _canCreateSymlinks = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _canCreateSymlinks = false;
        }
        finally
        {
            try { File.Delete(linkPath); } catch { /* best-effort probe cleanup */ }
            try { File.Delete(targetPath); } catch { /* best-effort probe cleanup */ }
        }
        return _canCreateSymlinks!.Value;
    }

    /// <summary>Attempts to create a symlink at <paramref name="path"/> pointing at
    /// <paramref name="targetWirePath"/> (a wire-format, forward-slash path exactly as the
    /// sender transmitted it -- only the separator character is converted, matching how a real
    /// Unix peer would also leave the target string otherwise untouched). Returns false with a
    /// human-readable <paramref name="error"/> instead of throwing when the privilege is
    /// missing or creation otherwise fails -- callers should skip the entry and warn, not abort
    /// the whole transfer, matching the plan's documented policy for this Windows-only gap.
    ///
    /// Always creates a FILE symlink (`File.CreateSymbolicLink`), never a directory symlink --
    /// Windows requires the caller to choose the link type up front (unlike Unix, where a
    /// symlink is type-agnostic), and the wire protocol carries no reliable signal for which the
    /// sender meant. File symlinks are the far more common real-world case; a symlink whose
    /// target turns out to be a directory won't traverse correctly on Windows as a result -- a
    /// documented limitation, not a silent one.</summary>
    public static bool TryCreateSymlink(string path, string targetWirePath, out string? error)
    {
        error = null;
        if (!CanCreateSymlinks())
        {
            error = "missing SeCreateSymbolicLinkPrivilege (enable Windows Developer Mode, or run elevated)";
            return false;
        }
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            string winTarget = targetWirePath.Replace('/', Path.DirectorySeparatorChar);
            File.CreateSymbolicLink(path, winTarget);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
