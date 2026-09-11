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
using RsyncWindows.Core.Daemon;

namespace RsyncWindows.Tray;

/// <summary>
/// The "which folders are visible and allowed" editor -- a grid over rsyncd.conf's `[module]`
/// sections (name, filesystem path, read-only, comment, and a comma-separated allowed-users
/// list). This form is only ever launched as the main form of a SEPARATELY elevated process
/// (see <see cref="Program"/>'s `--edit-folders` switch and <see cref="TrayApplicationContext"/>'s
/// menu item, which spawns it via `Verb = "runas"`) -- writing rsyncd.conf back out requires
/// administrator rights (the always-running, non-elevated tray process can't write it, only read
/// it -- see the ACL note in dist/install.ps1), and since exposing filesystem paths to the
/// network is inherently an administrative decision, requiring elevation to even open this editor
/// (not just to save) is the intended, not merely incidental, security posture.
/// </summary>
public sealed class ManageFoldersForm : Form
{
    private readonly BindingList<ModuleRow> _rows;
    private readonly DataGridView _grid;
    private readonly int _existingPort;

    private sealed class ModuleRow
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool ReadOnly { get; set; } = true;
        public string Comment { get; set; } = "";
        public string AllowedUsers { get; set; } = "";
    }

    public ManageFoldersForm()
    {
        Text = "rsyncWindows - Manage Shared Folders";
        Width = 820;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.SystemIcons.Application;

        RsyncdConfig config;
        try
        {
            config = File.Exists(RsyncdConfig.DefaultConfigPath) ? RsyncdConfig.Load(RsyncdConfig.DefaultConfigPath) : new RsyncdConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the existing config, starting from an empty list.\n\n{ex.Message}",
                "rsyncWindows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            config = new RsyncdConfig();
        }
        _existingPort = config.Port;

        _rows = new BindingList<ModuleRow>(config.Modules.Select(m => new ModuleRow
        {
            Name = m.Name,
            Path = m.Path,
            ReadOnly = m.ReadOnly,
            Comment = m.Comment,
            AllowedUsers = string.Join(", ", m.AuthUsers),
        }).ToList());

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            DataSource = _rows,
            RowHeadersWidth = 24,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ModuleRow.Name), HeaderText = "Module Name", Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ModuleRow.Path), HeaderText = "Folder Path", Width = 280 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ModuleRow.ReadOnly), HeaderText = "Read Only" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ModuleRow.AllowedUsers), HeaderText = "Allowed Users (comma-separated, blank = anyone)", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ModuleRow.Comment), HeaderText = "Comment", Width = 140 });

        var addButton = new Button { Text = "Add Folder...", Width = 110 };
        addButton.Click += (_, _) => AddFolder();
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
            Height = 28,
            Padding = new Padding(8, 6, 8, 0),
            Text = "Each row is a folder remote users can push to or pull from. \"Allowed Users\" names must match entries in Manage Allowed Users.",
        };

        Controls.Add(_grid);
        Controls.Add(helpLabel);
        Controls.Add(buttonPanel);
        CancelButton = cancelButton;
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder to share" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string suggestedName = System.IO.Path.GetFileName(dialog.SelectedPath.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(suggestedName))
            suggestedName = "module";
        suggestedName = MakeUniqueName(suggestedName);

        _rows.Add(new ModuleRow { Name = suggestedName, Path = dialog.SelectedPath, ReadOnly = true });
    }

    private string MakeUniqueName(string baseName)
    {
        string candidate = baseName;
        int n = 2;
        while (_rows.Any(r => string.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{baseName}{n++}";
        return candidate;
    }

    private void RemoveSelected()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
        {
            if (row.DataBoundItem is ModuleRow moduleRow)
                _rows.Remove(moduleRow);
        }
    }

    private void SaveAndClose()
    {
        _grid.EndEdit();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Path))
            {
                MessageBox.Show(this, "Every row needs both a module name and a folder path.", "rsyncWindows",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!names.Add(row.Name.Trim()))
            {
                MessageBox.Show(this, $"Module name \"{row.Name}\" is used more than once -- names must be unique.", "rsyncWindows",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var modules = _rows.Select(row =>
        {
            var users = row.AllowedUsers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new RsyncdModule
            {
                Name = row.Name.Trim(),
                Path = row.Path.Trim(),
                Comment = row.Comment?.Trim() ?? "",
                ReadOnly = row.ReadOnly,
                List = true,
                AuthUsers = users,
                SecretsFile = users.Length > 0 ? RsyncdConfig.DefaultSecretsPath : null,
            };
        }).ToList();

        try
        {
            RsyncdConfig.Save(RsyncdConfig.DefaultConfigPath, _existingPort, modules);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the config:\n\n{ex.Message}", "rsyncWindows",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
