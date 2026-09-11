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

using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using RsyncWindows.Core.Daemon;

namespace RsyncWindows.Tray;

/// <summary>
/// The "which users can transfer from the remote host" editor -- a grid over the shared
/// `rsyncd.secrets`-shaped user:password store (see <see cref="RsyncdSecretsFile"/>) that every
/// module's `auth users`/`secrets file` settings point at once a folder in
/// <see cref="ManageFoldersForm"/> has any allowed users configured. Like that form, this is only
/// ever the main form of a SEPARATELY elevated process (`--edit-users`, launched via
/// `Verb = "runas"`) -- the secrets file holds PLAINTEXT passwords (real rsync's own secrets-file
/// format has no hashing, matching authenticate.c's challenge/response, which needs the plaintext
/// secret to compute MD5(secret||challenge) against), so both reading and writing it here require
/// administrator rights; <see cref="LockDownAcl"/> additionally strips normal-user read access
/// from the file on disk after every save, mirroring real rsync's own insistence
/// (secrets.c/config.c) that a world-readable secrets file be rejected outright.
/// </summary>
public sealed class ManageUsersForm : Form
{
    private const string PasswordColumnName = "Password";

    private readonly BindingList<UserRow> _rows;
    private readonly DataGridView _grid;

    private sealed class UserRow
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public ManageUsersForm()
    {
        Text = "rsyncWindows - Manage Allowed Users";
        Width = 520;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.SystemIcons.Application;

        List<RsyncdSecretsFile.Entry> existing;
        try
        {
            existing = RsyncdSecretsFile.Load(RsyncdConfig.DefaultSecretsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the existing user list, starting empty.\n\n{ex.Message}",
                "rsyncWindows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            existing = [];
        }

        _rows = new BindingList<UserRow>(existing.Select(e => new UserRow { Username = e.User, Password = e.Password }).ToList());

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            DataSource = _rows,
            RowHeadersWidth = 24,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(UserRow.Username), HeaderText = "Username", Width = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = PasswordColumnName, DataPropertyName = nameof(UserRow.Password), HeaderText = "Password", Width = 200 });

        // Mask the password both at rest (CellFormatting, shown whenever the cell isn't being
        // actively edited) and while typing (EditingControlShowing's UseSystemPasswordChar) --
        // covering the two states a DataGridView cell can be in. Neither touches the underlying
        // UserRow.Password VALUE, only how it's rendered, so Save still writes the real password.
        _grid.CellFormatting += (_, e) =>
        {
            if (_grid.Columns[e.ColumnIndex].Name == PasswordColumnName && e.Value is string s && s.Length > 0)
            {
                e.Value = new string('●', s.Length);
                e.FormattingApplied = true;
            }
        };
        _grid.EditingControlShowing += (_, e) =>
        {
            if (_grid.CurrentCell?.OwningColumn?.Name == PasswordColumnName && e.Control is TextBox tb)
                tb.UseSystemPasswordChar = true;
        };

        var addButton = new Button { Text = "Add User", Width = 90 };
        addButton.Click += (_, _) => _rows.Add(new UserRow());
        var removeButton = new Button { Text = "Remove Selected", Width = 120 };
        removeButton.Click += (_, _) => RemoveSelected();
        var saveButton = new Button { Text = "Save", Width = 90 };
        saveButton.Click += (_, _) => SaveAndClose();
        var cancelButton = new Button { Text = "Cancel", Width = 90 };
        cancelButton.Click += (_, _) => Close();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(new Panel { Width = 20 });
        buttonPanel.Controls.Add(removeButton);
        buttonPanel.Controls.Add(addButton);

        var helpLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 0),
            Text = "These are the usernames/passwords remote clients authenticate with. Reference a username in a\nfolder's \"Allowed Users\" list (Manage Shared Folders) to require login for that folder.",
        };

        Controls.Add(_grid);
        Controls.Add(helpLabel);
        Controls.Add(buttonPanel);
        CancelButton = cancelButton;
    }

    private void RemoveSelected()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
        {
            if (row.DataBoundItem is UserRow userRow)
                _rows.Remove(userRow);
        }
    }

    private void SaveAndClose()
    {
        _grid.EndEdit();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.Username))
            {
                MessageBox.Show(this, "Every row needs a username.", "rsyncWindows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!names.Add(row.Username.Trim()))
            {
                MessageBox.Show(this, $"Username \"{row.Username}\" is used more than once -- names must be unique.", "rsyncWindows",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            var entries = _rows.Select(r => new RsyncdSecretsFile.Entry(r.Username.Trim(), r.Password));
            RsyncdSecretsFile.Save(RsyncdConfig.DefaultSecretsPath, entries);
            LockDownAcl(RsyncdConfig.DefaultSecretsPath);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the user list:\n\n{ex.Message}", "rsyncWindows",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Restricts the secrets file to Administrators + SYSTEM only -- the closest
    /// Windows-ACL equivalent of the owner-only (chmod 600) permissions real rsync's secrets.c
    /// requires on Unix, since the file holds plaintext passwords. Disables inheritance so a
    /// broader ACL on the containing %ProgramData%\rsyncWindows folder (which regular users can
    /// read/list, matching the config file's own permissions) doesn't leak read access back onto
    /// this specific file. Best-effort: a failure here shouldn't block the save itself having
    /// already succeeded, so it's caught separately from SaveAndClose's own try/catch by callers
    /// that care, but here it's allowed to bubble into the same error dialog since a lockdown
    /// failure on a secrets file is worth surfacing loudly.</summary>
    private static void LockDownAcl(string path)
    {
        var fileInfo = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        fileInfo.SetAccessControl(security);
    }
}
