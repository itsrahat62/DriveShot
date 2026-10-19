namespace DriveShot;

sealed class SettingsForm : Form
{
    readonly AppSettings _settings;
    readonly TextBox _scriptUrl = new() { Width = 380 }, _secret = new() { Width = 380, UseSystemPasswordChar = true };
    readonly TextBox _remote = new() { Width = 380 }, _folder = new() { Width = 380 }, _local = new() { Width = 380 }, _rclone = new() { Width = 380 };
    readonly CheckBox _public = new() { Text = "Make links public (anyone with the link can view)", AutoSize = true };
    readonly CheckBox _copy = new() { Text = "Copy the link to the clipboard after upload", AutoSize = true };
    readonly CheckBox _cursor = new() { Text = "Include the mouse cursor in screenshots", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start DriveShot with Windows", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Margin = new Padding(3, 6, 3, 3) };
    readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    readonly Button _save = new() { Text = "Save", AutoSize = true };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };

    public SettingsForm(AppSettings settings, Action? openConnect = null)
    {
        _settings = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "DriveShot Settings";
        Icon = AppIcon.Load(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(14);

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        for (int i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        void Row(string label, Control c, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 10, 7) }, 0, row);
            grid.Controls.Add(c, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            row++;
        }
        void Wide(Control c)
        {
            grid.Controls.Add(c, 0, row);
            grid.SetColumnSpan(c, 3);
            row++;
        }

        Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 3, 2) };
        Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 3, 4) };

        Wide(Heading("Upload with Google Apps Script (recommended)"));
        Wide(Hint("New here? Use the step-by-step setup. It creates the script for you and fills in these fields."));
        var connect = new Button { Text = "Connect Google Drive (step-by-step)…", AutoSize = true, Margin = new Padding(0, 0, 3, 6) };
        // Close first so this window can't later save stale values over what the setup wrote.
        connect.Click += (_, _) => { Close(); openConnect?.Invoke(); };
        connect.Enabled = openConnect != null;
        Wide(connect);
        Row("Web app URL:", _scriptUrl);
        Row("Secret:", _secret);
        Wide(Heading("…or upload with rclone (used when the URL is empty)"));
        Row("rclone remote:", _remote);
        Row("rclone.exe (blank = find it):", _rclone, BrowseButton(_rclone, folder: false));
        Wide(Heading("Folders"));
        Row("Drive folder link or ID:", _folder);
        Row("Keep local copies in:", _local, BrowseButton(_local, folder: true));
        Wide(Hint("Each screenshot is uploaded into a dated sub-folder (yyyy-MM-dd) inside the Drive folder."));
        Wide(_public);
        Wide(_copy);
        Wide(_cursor);
        Wide(_startup);
        Wide(_status);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _cancel, _save, _test });
        Wide(buttons);
        Controls.Add(grid);
        AcceptButton = _save;
        CancelButton = _cancel;

        _scriptUrl.Text = settings.AppsScriptUrl;
        _secret.Text = settings.AppsScriptSecret;
        _remote.Text = settings.RcloneRemote;
        _folder.Text = settings.DriveFolder;
        _local.Text = settings.LocalFolder;
        _rclone.Text = settings.RclonePath;
        _public.Checked = settings.PublicLink;
        _copy.Checked = settings.CopyLink;
        _cursor.Checked = settings.CaptureCursor;
        _startup.Checked = Autostart.IsEnabled();

        _save.Click += (_, _) => { Apply(_settings); _settings.Save(); Autostart.Set(_startup.Checked); Close(); };
        _cancel.Click += (_, _) => Close();
        _test.Click += async (_, _) => await TestAsync();
        ResumeLayout(false);
        PerformLayout();
    }

    static Button BrowseButton(TextBox target, bool folder)
    {
        var b = new Button { Text = "Browse…", AutoSize = true };
        b.Click += (_, _) =>
        {
            if (folder)
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = "Folder for local copies" };
                if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.SelectedPath;
            }
            else
            {
                using var dlg = new OpenFileDialog { Filter = "rclone.exe|rclone.exe|Programs (*.exe)|*.exe", FileName = target.Text };
                if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.FileName;
            }
        };
        return b;
    }

    void Apply(AppSettings s)
    {
        s.AppsScriptUrl = _scriptUrl.Text.Trim();
        s.AppsScriptSecret = _secret.Text.Trim();
        s.RcloneRemote = _remote.Text.Trim().TrimEnd(':');
        s.DriveFolder = DriveIds.Parse(_folder.Text);
        s.LocalFolder = _local.Text.Trim();
        s.RclonePath = _rclone.Text.Trim();
        s.PublicLink = _public.Checked;
        s.CopyLink = _copy.Checked;
        s.CaptureCursor = _cursor.Checked;
    }

    async Task TestAsync()
    {
        _test.Enabled = false;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Checking Google Drive…";
        try
        {
            var probe = _settings.Clone();
            Apply(probe);
            var (message, folderId) = await Drive.TestAsync(probe);
            if (string.IsNullOrWhiteSpace(_folder.Text) && folderId != null) _folder.Text = folderId;
            _status.ForeColor = Color.SeaGreen;
            _status.Text = "✓ " + message;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "✗ " + ex.Message;
        }
        finally { _test.Enabled = true; }
    }
}
