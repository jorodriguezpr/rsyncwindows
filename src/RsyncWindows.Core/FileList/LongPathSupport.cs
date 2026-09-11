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
/// Explicit `\\?\` prefixing for local file operations -- bypasses MAX_PATH (260 chars)
/// regardless of the machine-wide "Enable Win32 long paths" group policy, which is OFF by
/// default on a fresh Windows install and can't be assumed enabled on a destination machine.
/// See the plan's own flagged risk: "require long-path support... from the start rather than
/// retrofitting later."
///
/// An application-manifest-based approach (`longPathAware`, the usual .NET-recommended
/// alternative to prefixing every path by hand) was tried first and abandoned: it made
/// `rsyncWindows.exe` fail to start at all ("the application has failed to start because its
/// side-by-side configuration is incorrect") in this environment, for reasons not fully
/// diagnosed after a real attempt (a from-scratch minimal manifest, then the full standard
/// template with `trustInfo`/`requestedExecutionLevel`, both reproduced the same failure; a
/// clean rebuild ruled out stale build output). Explicit prefixing has no equivalent failure
/// mode -- it never touches process startup -- so it's the safer choice even though it's more
/// code to apply consistently. Applied at each project's central wire-path-to-local-path
/// resolver (`ResolveLocal`/`Resolve`/`ResolvePath`), so every File/Directory call downstream of
/// path resolution benefits without needing to touch each individual I/O call site.
/// </summary>
public static class LongPathSupport
{
    private const string LocalPrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Returns an absolute, `\\?\`-prefixed form of <paramref name="path"/> suitable
    /// for any Win32 file API -- idempotent (a path that's already prefixed, or already too
    /// short to matter, is still normalized consistently). `\\?\` paths bypass normal path
    /// processing entirely (no `.`/`..` resolution, forward slashes not accepted), so the input
    /// is fully resolved via <see cref="Path.GetFullPath(string)"/> FIRST -- this must be called
    /// with the complete path already assembled (e.g. after `Path.Combine`), not with a bare
    /// relative fragment.</summary>
    public static string Ensure(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(LocalPrefix, StringComparison.Ordinal))
            return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return UncPrefix + full[2..]; // \\server\share\... -> \\?\UNC\server\share\...
        return LocalPrefix + full;
    }
}
