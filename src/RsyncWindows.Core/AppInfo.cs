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

namespace RsyncWindows.Core;

/// <summary>Single source of truth for the product's version/author info -- used by the CLI's
/// <c>--version</c> output, the tray app's About dialog, and the dist docs, so all three stay
/// in agreement without hand-copying a version string into each.
///
/// Version numbering note: this codebase is versioned 2.x, not 1.x, because it is the SECOND
/// "rsync for Windows" the author has built. V1 was written entirely in C++Builder in 2016 as
/// the author's Master's in Computer Science project, was NOT wire-compatible with real Unix
/// rsync (its own private protocol), and shares no code with this project. This one -- a ground-up
/// C#/.NET rewrite that speaks real rsync's actual wire protocol (interoperating directly with
/// genuine Linux/macOS rsync installs) -- is versioned starting at 2.0 to reflect that it is a
/// total remake, not an upgrade of the 2016 codebase.</summary>
public static class AppInfo
{
    public const string ProductName = "rsyncWindows";
    public const string Version = "2.0.1";
    public const string Author = "Jose Rodriguez Arroyo";
    public const string Email = "jrpcone@gmail.com";
    public const string GitHub = "https://github.com/jorodriguezpr";
    public const string License = "Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)";

    public static string VersionLine => $"{ProductName} version {Version}";
    public static string AuthorLine => $"Author: {Author} <{Email}> {GitHub}";
    public static string LicenseLine => $"License: {License}";
    public static string FullBanner => $"{VersionLine}{Environment.NewLine}{AuthorLine}{Environment.NewLine}{LicenseLine}";
}
