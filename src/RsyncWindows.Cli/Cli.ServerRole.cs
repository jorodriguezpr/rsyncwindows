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

// The `rsyncWindows --server ...` role: what a real rsync peer spawns over a transport and
// wires to its stdin/stdout (Phase 4b direction -- a real client pushing TO us, or pulling
// FROM us with --sender). Parses the --server argv the same way real rsync's own server-side
// parse does (the bundled short-flags string is what ServerInvocationBuilder produces on the
// client side; both sides must derive identical preserve flags from it).

using RsyncWindows.Core.FileList;
using RsyncWindows.Core.Options;
using RsyncWindows.Core.Session;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Cli;

internal static class ServerRole
{
    public static int Run(string[] args)
    {
        // Shape: --server [--sender] <bundled-flags> . <path>
        bool amSender = args.Contains("--sender");
        var flags = args.FirstOrDefault(a => a.StartsWith('-') && a != "--sender" && a != "--server");
        bool preserveUid = flags != null && HasShortFlag(flags, 'o');
        bool preserveGid = flags != null && HasShortFlag(flags, 'g');
        bool compress = flags != null && HasShortFlag(flags, 'z');
        bool preserveTimes = flags != null && HasShortFlag(flags, 't');
        bool preservePerms = flags != null && HasShortFlag(flags, 'p');

        // args: [--server] [--sender] <flags> . <path...>; "." is the arg right before paths.
        int dot = Array.IndexOf(args, ".");
        if (dot < 0 || dot + 1 >= args.Length)
        {
            Console.Error.WriteLine("rsyncWindows: --server invocation missing paths");
            return 2;
        }

        var raw = new DuplexStreamPair(Console.OpenStandardInput(), Console.OpenStandardOutput());
        var session = ProtocolNegotiator.Negotiate(raw, isServer: true, compress: compress);

        if (amSender)
        {
            var encoder = new FileListEncoder();
            // The remote path is the transfer root; walk it and send the flist + files.
            string root = args[^1];
            var entries = FileListBuilder.Walk(root).ToList();
            foreach (var entry in entries)
                encoder.Write(session.Output, entry, preserveUid, preserveGid);
            encoder.WriteEndOfList(session.Output);
            IdListCodec.WriteIdLists(session.Output, entries, preserveUid, preserveGid, session.CompatFlags.HasFlag(CompatFlags.Id0Names));
            session.Output.Flush();

            // MUST be entries.Select(), NOT .Where(Regular).Select() -- see ClientRunner.PushSources'
            // matching comment: the peer's generator requests transfers by position in the full
            // flist we sent, so filtering out non-regular entries here desyncs every later index.
            var files = entries
                .Select(e => new SenderSession.FileToSend(e, e.FileType == RsyncFileType.Regular ? File.ReadAllBytes(Resolve(root, e.Path)) : []))
                .ToList();
            SenderSession.RunSenderLoop(session.Input, session.Output, files, session.ChecksumSeed, compress: session.CompressionEnabled);
            session.Output.Flush();
            return 0;
        }
        else
        {
            var received = ReceiverSession.RunReceiverLoop(
                session.Input, session.Output,
                path => ReadBasis(root: args[^1], path),
                session.ChecksumSeed,
                compress: session.CompressionEnabled,
                preserveUid: preserveUid,
                preserveGid: preserveGid,
                id0Names: session.CompatFlags.HasFlag(CompatFlags.Id0Names),
                onNonTransferEntry: e => ApplyNonTransferEntry(args[^1], e));
            foreach (var file in received)
            {
                string target = CliPath(args[^1], file.Entry.Path);
                if (!RsyncWindows.Core.FileList.FileWriteSupport.TryWriteFile(target, file.Data, out string? writeError))
                {
                    Console.Error.WriteLine($"rsyncWindows: skipped '{file.Entry.Path}': {writeError}");
                    continue;
                }
                if (preserveTimes)
                    RsyncWindows.Core.FileList.TimestampSupport.TrySetModTimeUtc(target, file.Entry.ModTimeUnix);
                if (preservePerms)
                    RsyncWindows.Core.FileList.PermissionSupport.TryApplyReadOnly(target, file.Entry.Mode);
            }
            return 0;
        }
    }

    /// <summary>Applies a Directory or Symlink flist entry directly to disk (see
    /// DaemonConnectionHandler's twin of this method for the full rationale). Both degrade
    /// gracefully -- log to stderr and skip the entry -- rather than aborting the whole transfer:
    /// symlinks when SeCreateSymbolicLinkPrivilege is unavailable, directories when the peer's
    /// name is legal on ITS filesystem but not NTFS (see FileWriteSupport's doc comment).</summary>
    private static void ApplyNonTransferEntry(string root, FileEntry entry)
    {
        string full = CliPath(root, entry.Path);
        if (entry.FileType == RsyncFileType.Directory)
        {
            if (!RsyncWindows.Core.FileList.FileWriteSupport.TryCreateDirectory(full, out string? error))
                Console.Error.WriteLine($"rsyncWindows: skipped directory '{entry.Path}': {error}");
        }
        else if (entry.FileType == RsyncFileType.Symlink && entry.SymlinkTarget != null)
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !RsyncWindows.Core.FileList.FileWriteSupport.TryCreateDirectory(dir, out string? dirError))
            {
                Console.Error.WriteLine($"rsyncWindows: skipped symlink '{entry.Path}': {dirError}");
                return;
            }
            if (!RsyncWindows.Core.FileList.SymlinkSupport.TryCreateSymlink(full, entry.SymlinkTarget, out string? error))
                Console.Error.WriteLine($"rsyncWindows: skipped symlink '{entry.Path}': {error}");
        }
    }

    private static byte[] ReadBasis(string root, string path)
    {
        string full = Resolve(root, path);
        return File.Exists(full) ? File.ReadAllBytes(full) : [];
    }

    private static string Resolve(string root, string wirePath)
    {
        string rel = wirePath.Replace('/', Path.DirectorySeparatorChar);
        if (rel.Contains(".."))
            throw new InvalidOperationException($"unsafe path from peer: {wirePath}");
        // \\?\-prefixed so every downstream File/Directory call bypasses MAX_PATH -- see
        // LongPathSupport's doc comment for why (an app-manifest approach broke the exe outright).
        return RsyncWindows.Core.FileList.LongPathSupport.Ensure(Path.Combine(root, rel));
    }

    private static string CliPath(string root, string wirePath) => Resolve(root, wirePath);

    /// <summary>Mirror of DaemonHandshake.HasShortFlag / DaemonConnectionHandler.HasShortFlag:
    /// scans a bundled short-option token (e.g. "-vtpze32.0fxCIvu") for one flag letter, stopping
    /// at 'e' (the protocol/capability suffix starts there, and isn't real option letters).</summary>
    private static bool HasShortFlag(string arg, char flag)
    {
        if (arg.Length < 2 || arg[0] != '-' || arg[1] == '-')
            return false;
        for (int i = 1; i < arg.Length; i++)
        {
            if (arg[i] == 'e')
                return false;
            if (arg[i] == flag)
                return true;
        }
        return false;
    }
}