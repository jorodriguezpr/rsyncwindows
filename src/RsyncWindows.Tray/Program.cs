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

namespace RsyncWindows.Tray;

static class Program
{
    /// <summary>
    ///  The main entry point for the application. Two extra modes, both launched as a SEPARATE,
    ///  elevated process (see <see cref="TrayApplicationContext"/>'s menu handlers, which spawn
    ///  `Verb = "runas"` child processes rather than running these forms in-process): `--edit-users`
    ///  and `--edit-folders` show just that one editor form as the whole app, instead of the tray
    ///  icon. Writing rsyncd.conf/rsyncd.secrets needs administrator rights (see the ACL notes on
    ///  those forms and in dist/install.ps1), while the normal tray icon itself deliberately runs
    ///  at the logged-in user's own integrity level so it doesn't need a UAC prompt just to show
    ///  status or start automatically at login.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0] == "--edit-users")
        {
            Application.Run(new ManageUsersForm());
            return;
        }
        if (args.Length > 0 && args[0] == "--edit-folders")
        {
            Application.Run(new ManageFoldersForm());
            return;
        }

        Application.Run(new TrayApplicationContext());
    }
}