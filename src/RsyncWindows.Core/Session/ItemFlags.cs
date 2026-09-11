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

namespace RsyncWindows.Core.Session;

/// <summary>Per-file transfer-negotiation flags exchanged via read/write_ndx_and_attrs
/// (rsync.h:244-263). Only the subset this v1 acts on is enumerated.</summary>
[Flags]
public enum ItemFlags : ushort
{
    None = 0,
    BasisTypeFollows = 1 << 11,
    XNameFollows = 1 << 12,
    IsNew = 1 << 13,
    LocalChange = 1 << 14,
    Transfer = 1 << 15,
}
