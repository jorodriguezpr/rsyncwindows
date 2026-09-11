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

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Transports;

/// <summary>
/// Spawns a remote-shell command as a child process and wires its stdin/stdout directly as
/// the raw (pre-negotiation) transport -- this is how real rsync's own `-e ssh`/plain
/// `host:path` invocation works (main.c's do_cmd(): `ssh host rsync --server &lt;opts&gt; . &lt;path&gt;`,
/// no daemon greeting at all, straight into <see cref="ProtocolNegotiator"/>).
///
/// The remote-shell program is a parameter, not hardcoded to "ssh": production use passes
/// "ssh" (or a user's --rsh override); the Phase 4a interop test against the WSL2-built
/// reference rsync binary passes "wsl.exe" instead, since there's no real second host --
/// exactly the same process-spawn-and-wire-stdio mechanics apply either way.
///
/// Interactive password/host-key prompts: on Unix, real rsync keeps these completely separate
/// from the piped protocol data -- ssh opens /dev/tty directly for a prompt, while stdin/stdout
/// stay dedicated to the rsync stream. Windows' OpenSSH port does the exact same thing: its
/// `_PATH_TTY` is literally defined as `"conin$"` (see PowerShell/openssh-portable's defines.h),
/// so it opens CONIN$/CONOUT$ directly for prompts regardless of what its own stdin/stdout are
/// redirected to -- and per Microsoft's own docs, `CreateFile("CONIN$", ...)` is explicitly
/// supported "even if STDIN and STDOUT have been redirected", with no requirement that the
/// console be exclusive to one process. So sharing our own console with the child (no special
/// creation flag; ssh.exe just inherits it, same as any normal child process) is the
/// textbook-correct, documented approach here -- not a workaround.
///
/// An EARLIER attempt at exactly this (via System.Diagnostics.Process.Start, before this class
/// was rewritten to spawn natively) hung anyway: the prompt displayed but the child never saw
/// typed keystrokes. That failure was never conclusively root-caused against the Win32/OpenSSH
/// documentation above (nothing there predicts it), so it's suspected to have been specific to
/// some detail of Process.Start's own handle setup rather than a fundamental limitation --
/// CREATE_NEW_CONSOLE (a fully separate console/window for the child) was shipped as a working,
/// if less clean, fallback while that stayed unresolved. This is the natural retry now that the
/// spawn path has already been rewritten to go through CreateProcessW directly (see
/// <see cref="Win32Interop"/>) for unrelated reasons -- if inheriting the console still doesn't
/// work here either, revert `dwCreationFlags` below to <see cref="Win32Interop.CREATE_NEW_CONSOLE"/>
/// (confirmed working, just with a second window for prompts).
/// </summary>
public sealed class SshProcessTransport(string program, IReadOnlyList<string> args) : IDisposable
{
    private AnonymousPipeServerStream? _stdinPipe;
    private AnonymousPipeServerStream? _stdoutPipe;
    private AnonymousPipeServerStream? _stderrPipe;
    private IntPtr _processHandle;
    private Task? _stderrReaderTask;

    public DuplexStreamPair Connect()
    {
        _stdinPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var startupInfo = new Win32Interop.STARTUPINFOW
        {
            cb = Marshal.SizeOf<Win32Interop.STARTUPINFOW>(),
            dwFlags = Win32Interop.STARTF_USESTDHANDLES,
            hStdInput = _stdinPipe.ClientSafePipeHandle.DangerousGetHandle(),
            hStdOutput = _stdoutPipe.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = _stderrPipe.ClientSafePipeHandle.DangerousGetHandle(),
        };

        var commandLineBuffer = new StringBuilder(Win32Interop.BuildCommandLine(program, args));

        bool created = Win32Interop.CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: commandLineBuffer,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: true,
            dwCreationFlags: 0, // inherit our own console -- see class doc comment
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: null,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out var processInfo);

        if (!created)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"failed to start process: {program} (Win32 error {error})");
        }

        _processHandle = processInfo.hProcess;
        Win32Interop.CloseHandle(processInfo.hThread);

        // The child received its own duplicated copies of the pipe handles via inheritance --
        // drop our copies of ITS ends now so we can detect EOF once the child actually exits,
        // rather than keeping a spare handle open on our side forever.
        _stdinPipe.DisposeLocalCopyOfClientHandle();
        _stdoutPipe.DisposeLocalCopyOfClientHandle();
        _stderrPipe.DisposeLocalCopyOfClientHandle();

        // Surface the remote's stderr (rsync's own diagnostic/error output) without letting it
        // block the process -- read it on a background task rather than synchronously.
        _stderrReaderTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(_stderrPipe);
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                Console.Error.WriteLine($"[remote] {line}");
        });

        return new DuplexStreamPair(_stdoutPipe, _stdinPipe);
    }

    public int WaitForExit()
    {
        if (_processHandle == IntPtr.Zero)
            return -1;

        Win32Interop.WaitForSingleObject(_processHandle, Win32Interop.INFINITE);
        Win32Interop.GetExitCodeProcess(_processHandle, out uint exitCode);
        try
        {
            _stderrReaderTask?.Wait();
        }
        catch (AggregateException)
        {
            // stderr pipe was torn down along with everything else -- not worth surfacing here.
        }
        return (int)exitCode;
    }

    public void Dispose()
    {
        if (_processHandle != IntPtr.Zero)
        {
            Win32Interop.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        _stdinPipe?.Dispose();
        _stdoutPipe?.Dispose();
        _stderrPipe?.Dispose();
    }
}
