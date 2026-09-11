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

namespace RsyncWindows.Core.Options;

public enum DeleteTiming { Default, Before, During, Delay, After }

/// <summary>Parsed result of <see cref="RsyncArgParser"/>, covering the ~25-option common
/// set the plan scopes for v1 (see the plan's "Common/core" list). Advanced/niche options
/// (ACLs, xattrs, hardlinks, batch mode, --link-dest family, etc.) are deliberately absent.</summary>
public sealed class RsyncOptions
{
    public int Verbose { get; set; }
    public bool DryRun { get; set; }

    // Individual attrs that -a/--archive expands to (-rlptgoD) -- set directly by the parser,
    // matching options.c's case 'a' handling rather than being derived after the fact.
    public bool Recursive { get; set; }
    public bool PreserveLinks { get; set; }
    public bool PreservePerms { get; set; }
    public bool PreserveTimes { get; set; }
    public bool PreserveOwner { get; set; }
    public bool PreserveGroup { get; set; }
    public bool PreserveDevices { get; set; }
    public bool PreserveSpecials { get; set; }

    public bool Compress { get; set; }
    public int? CompressLevel { get; set; }
    public bool WholeFileChecksum { get; set; } // -c/--checksum

    public bool Delete { get; set; }
    public DeleteTiming DeleteTiming { get; set; } = DeleteTiming.Default;
    public bool DeleteExcluded { get; set; }

    public List<string> ExcludePatterns { get; } = [];
    public List<string> IncludePatterns { get; } = [];
    public List<string> ExcludeFromFiles { get; } = [];
    public List<string> IncludeFromFiles { get; } = [];
    public List<string> FilterRules { get; } = [];

    public bool Partial { get; set; }
    public string? PartialDir { get; set; }
    public bool Progress { get; set; }
    public bool Stats { get; set; }
    public bool HumanReadable { get; set; }
    public bool ItemizeChanges { get; set; }
    public long? BwLimitKBytes { get; set; }

    public string? RemoteShell { get; set; } // -e/--rsh
    public string? RsyncPath { get; set; }
    public string? PasswordFile { get; set; } // --password-file, daemon-mode auth

    public bool Update { get; set; } // -u
    public bool ExistingOnly { get; set; }
    public bool IgnoreExisting { get; set; }
    public bool Relative { get; set; } // -R

    public string? FilesFrom { get; set; }
    public bool Backup { get; set; }
    public string? BackupDir { get; set; }

    public string? OutFormat { get; set; }
    public string? LogFile { get; set; }

    public List<string> Sources { get; } = [];
    public string Destination { get; set; } = "";
}
