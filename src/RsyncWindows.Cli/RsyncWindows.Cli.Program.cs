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

// rsyncWindows.exe entry point: argv dispatch -- --version / --server role / client role.
// The client composition lives in Cli.ClientRunner.cs, the server role in Cli.ServerRole.cs.

using System.Text;
using RsyncWindows.Core;
using RsyncWindows.Core.Options;

namespace RsyncWindows.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            if (args[0] is "--version" or "-V")
            {
                Console.WriteLine($"{AppInfo.VersionLine} (protocol 32)");
                Console.WriteLine(AppInfo.AuthorLine);
                Console.WriteLine(AppInfo.LicenseLine);
                return 0;
            }

            if (args[0] is "--author" or "--about")
            {
                Console.WriteLine(AppInfo.AuthorLine);
                Console.WriteLine(AppInfo.LicenseLine);
                return 0;
            }

            // Real rsync's own argv[0] check: "rsync --server ..." turns the process into the
            // remote-side server role -- we're being spawned BY a peer over a transport.
            if (args[0] == "--server")
                return ServerRole.Run(args);

            var options = RsyncArgParser.Parse(args);
            if (options.Sources.Count == 0)
                throw new RsyncArgException("no source specified");

            return new ClientRunner(options).Run();
        }
        catch (RsyncArgException ex)
        {
            Console.Error.WriteLine($"rsyncWindows: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rsyncWindows: {ex.Message}");
            return ex is IOException or InvalidDataException ? 12 : 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: rsyncWindows [OPTION]... SRC DEST");
        Console.WriteLine("  -V, --version          print version number and author info, then exit");
        Console.WriteLine("      --author, --about  print author info, then exit");
        Console.WriteLine("  -v, --verbose          increase verbosity");
        Console.WriteLine("  -a, --archive          archive mode; equals -rlptgoD");
        Console.WriteLine("  -r, --recursive        recurse into directories");
        Console.WriteLine("  -z, --compress         compress file data during transfer");
        Console.WriteLine("  -n, --dry-run          show what would have been transferred");
        Console.WriteLine("  -e, --rsh=COMMAND      specify the remote shell to use");
        Console.WriteLine("      --delete           delete extraneous files from dest dirs");
        Console.WriteLine("      --exclude=PATTERN  exclude files matching PATTERN");
        Console.WriteLine("      --stats            give some file-transfer stats");
        Console.WriteLine("  SRC/DEST: local path, user@host:path, host::module/path, or rsync://host[:port]/module/path");
    }
}