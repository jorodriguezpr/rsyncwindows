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
/// Unix mode_t type-bit constants (S_IFMT family) and sensible default permission bits for a
/// platform (Windows) that has no real Unix mode of its own. Wire format needs the real bits
/// (a Linux peer reads and acts on them), so v1 synthesizes conventional values rather than
/// omitting them.
/// </summary>
public static class UnixMode
{
    public const int TypeMask = 0xF000; // S_IFMT (0170000 octal)
    public const int TypeRegular = 0x8000; // S_IFREG (0100000 octal)
    public const int TypeDirectory = 0x4000; // S_IFDIR (0040000 octal)
    public const int TypeSymlink = 0xA000; // S_IFLNK (0120000 octal)

    public const int RegularFileDefault = TypeRegular | 0x1B6; // 0100644
    public const int DirectoryDefault = TypeDirectory | 0x1ED; // 0040755
    public const int SymlinkDefault = TypeSymlink | 0x1FF;     // 0120777 -- conventional; permissions
                                                                // on a symlink itself are normally ignored

    public static RsyncFileType ToFileType(int mode) => (mode & TypeMask) switch
    {
        TypeDirectory => RsyncFileType.Directory,
        TypeSymlink => RsyncFileType.Symlink,
        _ => RsyncFileType.Regular,
    };

    /// <summary>Port of to_wire_mode()/from_wire_mode() (ifuncs.h) -- identity on platforms
    /// where S_IFLNK is already 0120000, which is every platform this project targets/talks to.</summary>
    public static int ToWireMode(int mode) => mode;
    public static int FromWireMode(int mode) => mode;
}
