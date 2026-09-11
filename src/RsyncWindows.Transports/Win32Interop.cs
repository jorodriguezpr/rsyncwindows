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

using System.Runtime.InteropServices;
using System.Text;

namespace RsyncWindows.Transports;

/// <summary>
/// The small native surface <see cref="SshProcessTransport"/> needs to spawn a child process with
/// its standard handles pointed at our own pipes (as <see cref="System.Diagnostics.Process"/>
/// already supports) while ALSO giving it a brand-new, separate console via
/// <see cref="CREATE_NEW_CONSOLE"/> -- something <see cref="System.Diagnostics.ProcessStartInfo"/>
/// has no flag for. Kept intentionally tiny: just enough of CreateProcessW to do that one thing.
/// </summary>
internal static class Win32Interop
{
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    internal const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    private static readonly char[] CharsRequiringQuoting = [' ', '\t', '\n', '\v', '"'];

    /// <summary>Builds a single CreateProcessW-style command line from a program and its argument
    /// list, using the same backslash/quote escaping rules the Win32 C runtime (and
    /// CommandLineToArgvW) expects -- ported from the well-known algorithm .NET's own
    /// <c>Process.ArgumentList</c> uses internally, since CreateProcessW takes one flat string,
    /// not an argv array.</summary>
    internal static string BuildCommandLine(string program, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, program);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny(CharsRequiringQuoting) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int idx = 0;
        while (idx < argument.Length)
        {
            char c = argument[idx++];
            if (c == '\\')
            {
                int backslashCount = 1;
                while (idx < argument.Length && argument[idx] == '\\')
                {
                    backslashCount++;
                    idx++;
                }

                if (idx == argument.Length)
                    sb.Append('\\', backslashCount * 2);
                else if (argument[idx] == '"')
                {
                    sb.Append('\\', backslashCount * 2 + 1);
                    sb.Append('"');
                    idx++;
                }
                else
                    sb.Append('\\', backslashCount);
            }
            else if (c == '"')
                sb.Append('\\').Append('"');
            else
                sb.Append(c);
        }
        sb.Append('"');
    }
}
