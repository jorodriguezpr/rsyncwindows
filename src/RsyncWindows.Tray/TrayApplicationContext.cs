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

using System.ServiceProcess;
using Microsoft.Win32;
using RsyncWindows.Core.Daemon;

namespace RsyncWindows.Tray;

/// <summary>
/// The tray icon and its context menu -- no main window, matching the plan's "plain WinForms
/// (NotifyIcon)" scope for Phase 5: status for the local daemon Service (start/stop, a live
/// running/stopped label) plus quick access to its config file, since a Windows Service has no
/// desktop session of its own to show UI from. Client-transfer progress (the other half of the
/// plan's description) has nothing to show yet -- the CLI client isn't wired up as of Phase 5 --
/// so this is deliberately just the Service-admin half for now; the balloon-tip/progress hooks
/// for a running client transfer are a natural follow-up once the CLI exists.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "rsyncWindows";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "rsyncWindows Tray";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly System.Windows.Forms.Timer _statusTimer;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Service: checking...") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start Service", null, (_, _) => StartService());
        _stopItem = new ToolStripMenuItem("Stop Service", null, (_, _) => StopService());
        var manageFoldersItem = new ToolStripMenuItem("Manage Shared Folders...", null, (_, _) => LaunchEditor("--edit-folders", "the folders editor"));
        var manageUsersItem = new ToolStripMenuItem("Manage Allowed Users...", null, (_, _) => LaunchEditor("--edit-users", "the users editor"));
        var editConfigItem = new ToolStripMenuItem("Edit Config (raw text)...", null, (_, _) => EditConfig());
        var openFolderItem = new ToolStripMenuItem("Open Config Folder", null, (_, _) => OpenConfigFolder());
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows())
        {
            Checked = IsStartWithWindowsEnabled(),
        };
        var aboutItem = new ToolStripMenuItem("About rsyncWindows...", null, (_, _) => ShowAbout());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(manageFoldersItem);
        menu.Items.Add(manageUsersItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(editConfigItem);
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "rsyncWindows",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => LaunchEditor("--edit-folders", "the folders editor");

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
    }

    /// <summary>Launches a SEPARATE elevated copy of this same exe pointed at one of the
    /// `--edit-*` editor forms (see <see cref="Program.Main"/>) -- the always-running tray
    /// process itself stays at the logged-in user's normal integrity level (so it can start
    /// silently at login with no UAC prompt), while editing rsyncd.conf/rsyncd.secrets needs
    /// administrator rights. <c>UseShellExecute = true</c> + <c>Verb = "runas"</c> is what
    /// actually triggers the UAC prompt; a cancelled prompt surfaces as a Win32Exception with
    /// code 1223 (ERROR_CANCELLED), which is deliberately swallowed rather than shown as an
    /// error balloon -- declining the prompt isn't a failure, it's just "never mind".</summary>
    private void LaunchEditor(string arg, string friendlyName)
    {
        try
        {
            string exePath = Application.ExecutablePath;
            var psi = new System.Diagnostics.ProcessStartInfo(exePath, arg)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // user declined the UAC prompt -- nothing to report
        }
        catch (Exception ex)
        {
            ShowBalloon($"Couldn't open {friendlyName}", ex.Message, ToolTipIcon.Error);
        }
    }

    private static bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private void ToggleStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (_startWithWindowsItem.Checked)
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            else
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            _startWithWindowsItem.Checked = !_startWithWindowsItem.Checked;
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't change startup setting", ex.Message, ToolTipIcon.Error);
        }
    }

    private void RefreshStatus()
    {
        var status = TryGetServiceStatus();
        if (status == null)
        {
            _statusItem.Text = "Service: not installed";
            _icon.Text = "rsyncWindows (service not installed)";
            _startItem.Enabled = false;
            _stopItem.Enabled = false;
            return;
        }

        bool running = status == ServiceControllerStatus.Running;
        _statusItem.Text = $"Service: {status}";
        _icon.Text = $"rsyncWindows ({status})";
        _startItem.Enabled = status is ServiceControllerStatus.Stopped;
        _stopItem.Enabled = running;
    }

    private static ServiceControllerStatus? TryGetServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // service isn't installed
        }
    }

    private void StartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't start rsyncWindows", ex.Message, ToolTipIcon.Error);
        }
        RefreshStatus();
    }

    private void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't stop rsyncWindows", ex.Message, ToolTipIcon.Error);
        }
        RefreshStatus();
    }

    private void EditConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(RsyncdConfig.DefaultConfigPath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(RsyncdConfig.DefaultConfigPath))
                File.WriteAllText(RsyncdConfig.DefaultConfigPath, "# rsyncWindows daemon config -- see the Service's starter comment for the module syntax.\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RsyncdConfig.DefaultConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't open config", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenConfigFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(RsyncdConfig.DefaultConfigPath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Couldn't open folder", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(4000, title, text, icon);

    /// <summary>Loads the real rsyncWindows.ico shipped alongside the tray exe (see the
    /// project's `&lt;None Include="...rsyncWindows.ico" CopyToOutputDirectory="PreserveNewest"&gt;`
    /// item) rather than <see cref="Icon.ExtractAssociatedIcon"/> off the running exe -- more
    /// reliable under a self-contained single-file publish, where the exe's own embedded
    /// resources aren't always extractable the same way a loose file on disk is. Falls back to
    /// the generic system icon if the file is ever missing, so a packaging mistake degrades
    /// gracefully instead of crashing the tray on startup.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "rsyncWindows.ico");
            if (File.Exists(path))
                return new Icon(path);
        }
        catch
        {
            // fall through to the system default below
        }
        return SystemIcons.Application;
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            $"{RsyncWindows.Core.AppInfo.ProductName} {RsyncWindows.Core.AppInfo.Version}\n\n" +
            "An rsync-compatible file sync client, daemon, and tray status app for Windows.\n\n" +
            $"Author: {RsyncWindows.Core.AppInfo.Author}\n" +
            $"Email: {RsyncWindows.Core.AppInfo.Email}\n" +
            $"GitHub: {RsyncWindows.Core.AppInfo.GitHub}\n\n" +
            $"License: {RsyncWindows.Core.AppInfo.License}",
            "About rsyncWindows",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
