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

/// <summary>Multiplex message-channel tags (rsync.h enum msgcode, values matching enum logcode).</summary>
public enum MsgCode
{
    Data = 0,          // raw data on the multiplexed stream
    ErrorXfer = 1,     // sent over socket for any protocol
    Info = 2,
    Error = 3,         // sent over socket for protocols >= 30
    Warning = 4,
    ErrorSocket = 5,   // sibling logging only
    Log = 6,
    Client = 7,        // never transmitted
    ErrorUtf8 = 8,     // sibling logging only
    Redo = 9,          // reprocess indicated flist index
    Stats = 10,        // stats data for generator
    IoError = 22,      // sending side had an I/O error
    IoTimeout = 33,    // daemon's timeout value
    Noop = 42,         // legacy protocol-30 only
    ErrorExit = 86,    // synchronize an error exit
    Success = 100,     // successfully updated indicated flist index
    Deleted = 101,     // successfully deleted a file on receiving side
    NoSend = 102,      // sender failed to open a file we wanted
}

/// <summary>Base added to a MsgCode before shifting into the top byte of a multiplex frame header (rsync.h:210).</summary>
public static class Mplex
{
    public const int Base = 7;
}
