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

/// <summary>File-list entry flag bits (rsync.h:47-73). Only the subset this v1 encoder/decoder
/// actually uses is enumerated; the rest (hardlinks, rdev, atime, crtime, nsec, name-follows)
/// are recorded in comments for when those features are added.</summary>
[Flags]
public enum XmitFlags
{
    None = 0,
    TopDir = 1 << 0,
    SameMode = 1 << 1,
    ExtendedFlags = 1 << 2,       // protocols 28+ ("XMIT_SAME_RDEV_pre28" on 20-27, unused here)
    SameUid = 1 << 3,
    SameGid = 1 << 4,
    SameName = 1 << 5,
    LongName = 1 << 6,
    SameTime = 1 << 7,
    NoContentDir = 1 << 8,        // dirs only, protocol 30+ (we always leave this unset -- see FileListEncoder)
    // 1<<9 XMIT_HLINKED, 1<<10 XMIT_USER_NAME_FOLLOWS, 1<<11 XMIT_GROUP_NAME_FOLLOWS,
    // 1<<12 XMIT_HLINK_FIRST -- deliberately unused by this v1 encoder/decoder.
    ModNsec = 1 << 13,             // NOT gated by any CLI option -- set whenever a file's mtime
                                   // has a nonzero nanosecond component and protocol >= 31 (see
                                   // flist.c's NSEC_BUMP check), so a real peer sends this
                                   // routinely; MUST be handled by the decoder (confirmed via
                                   // Phase 4b interop: a real client's very first file-list
                                   // entry set it, silently desyncing every field after mtime
                                   // until this was added) even though our own encoder never
                                   // sets it (Windows file time handling doesn't track/send
                                   // sub-second precision in this v1).
    // 1<<14 XMIT_SAME_ATIME, 1<<17 XMIT_CRTIME_EQ_MTIME -- gated by preserve_atimes/crtimes,
    // which nothing in this v1's option set ever requests, so real peers never send them to us
    // in a v1-driven exchange -- deliberately unused.
}
