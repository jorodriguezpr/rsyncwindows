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

namespace RsyncWindows.Core.Daemon;

/// <summary>
/// One `[modulename]` section of an rsyncd.conf-shaped config -- the subset of loadparm.c's
/// per-module parameters this v1 actually enforces. Scoped to what's meaningful on Windows and
/// achievable without a chroot/setuid-equivalent: no `uid`/`gid`/`chroot`/`exclude`/`include`/
/// `filter`/`lock file` (see the plan's risk #2 and #7 for why user/permission mapping is
/// deliberately best-effort-or-absent). "read only" defaults to true, matching rsyncd.conf's
/// own documented default -- an operator must opt in to accepting uploads.
/// </summary>
public sealed class RsyncdModule
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Comment { get; init; } = "";
    public bool ReadOnly { get; init; } = true;
    public bool List { get; init; } = true;
    public IReadOnlyList<string> HostsAllow { get; init; } = [];
    public IReadOnlyList<string> HostsDeny { get; init; } = [];
    public IReadOnlyList<string> AuthUsers { get; init; } = [];
    public string? SecretsFile { get; init; }
    public int MaxConnections { get; init; } = 0; // 0 = unlimited, matching lp_max_connections' default
    public int TimeoutSeconds { get; init; } = 0; // 0 = no timeout

    public bool RequiresAuth => AuthUsers.Count > 0;
}
