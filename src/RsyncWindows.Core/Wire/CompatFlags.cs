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

namespace RsyncWindows.Core.Wire;

/// <summary>Protocol-30+ capability bits negotiated during setup_protocol (compat.c:118-126).</summary>
[Flags]
public enum CompatFlags
{
    None = 0,
    IncRecurse = 1 << 0,
    SymlinkTimes = 1 << 1,
    SymlinkIconv = 1 << 2,
    SafeFlist = 1 << 3,
    AvoidXattrOptim = 1 << 4,
    ChecksumSeedFix = 1 << 5,   // "proper_seed_order": seed is hashed BEFORE data, not after
    InplacePartialDir = 1 << 6,
    VarintFlistFlags = 1 << 7, // flips file-list xflags encoding from byte to varint
    Id0Names = 1 << 8,
}
