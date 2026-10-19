using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DriveShot;

/// <summary>
/// Step-by-step setup of the Apps Script upload endpoint in the user's own Google account.
/// The script is generated with the folder ID and a private key already filled in.
/// </summary>
sealed class ConnectForm : Form
{
    /// <summary>Raised with the Drive folder name once the connection test passes.</summary>
    public event Action<string>? Connected;

    readonly AppSettings _settings;
    readonly string _secret;
    readonly TextBox _folder = new() { Width = 440 };
    readonly TextBox _url = new() { Width = 440 };
    readonly Button _copy = new() { Text = "Copy script", AutoSize = true };
    readonly Button _view = new() { Text = "View script", AutoSize = true };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    readonly Button _close = new() { Text = "Close", AutoSize = true };
    readonly Label _folderNote = Note(), _copied = Note(), _status = Note();

    public ConnectForm(AppSettings settings)
    {
        _settings = settings;
        _secret = string.IsNullOrWhiteSpace(settings.AppsScriptSecret) ? NewSecret() : settings.AppsScriptSecret;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Connect Google Drive";
        Icon = AppIcon.Load(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(18, 12, 18, 12);

        var page = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        void Add(Control c) => page.Controls.Add(c);

        Add(new Label
        {
            Text = "DriveShot uploads through a small script that runs in your own Google account, so your screenshots stay in your Drive. " +
                   "It takes about 3 minutes and you only do it once.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 2),
        });
        var guide = new LinkLabel { Text = "Read the full guide online", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        guide.LinkClicked += (_, _) => Shell.Open("https://github.com/itsrahat62/DriveShot/blob/main/docs/connect-google-drive.md");
        Add(guide);

        Add(Step("1.  Choose the Drive folder"));
        Add(Hint("Open Google Drive, create or open the folder for your screenshots, and copy its link from the address bar (it contains /folders/)."));
        Add(Line(_folder, MakeButton("Open Google Drive", () => Shell.Open("https://drive.google.com/drive/my-drive"))));
        Add(_folderNote);

        Add(Step("2.  Copy the upload script"));
        Add(Hint("DriveShot puts your folder ID and a private key into the script for you. There is nothing to edit."));
        Add(Line(_copy, _view, _copied));

        Add(Step("3.  Create the Apps Script"));
        Add(Hint("Click Open Apps Script and sign in with the Google account that owns the folder. " +
                 "In the editor press Ctrl+A, then Ctrl+V to replace everything with the script, and click Save (the disk icon)."));
        Add(Line(MakeButton("Open Apps Script", () => Shell.Open("https://script.new"))));

        Add(Step("4.  Deploy it as a web app"));
        Add(Hint("•  Click Deploy → New deployment.\n" +
                 "•  Click the gear icon next to “Select type” and choose Web app.\n" +
                 "•  Set  Execute as: Me  and  Who has access: Anyone.\n" +
                 "•  Click Deploy → Authorize access → choose your account.\n" +
                 "•  If Google says “Google hasn’t verified this app”, click Advanced → Go to … (unsafe) → Allow.\n" +
                 "    It is your own script and it only touches the folder you chose.\n" +
                 "•  Copy the Web app URL (it ends with /exec)."));

        Add(Step("5.  Paste the Web app URL"));
        Add(Line(_url, _connect));
        Add(_status);

        var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(_close);
        Add(bottom);
        Controls.Add(page);
        CancelButton = _close;

        _folder.Text = settings.DriveFolder;
        _url.Text = settings.AppsScriptUrl;
        _folder.TextChanged += (_, _) => UpdateState();
        _url.TextChanged += (_, _) => UpdateState();
        _copy.Click += (_, _) => CopyScript();
        _view.Click += (_, _) => ViewScript();
        _connect.Click += async (_, _) => await ConnectAsync();
        _close.Click += (_, _) => Close();
        UpdateState();
        ResumeLayout(false);
        PerformLayout();
    }

    // ---------- layout helpers ----------

    static Label Step(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5F),
        Margin = new Padding(0, 10, 0, 2),
    };

    static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(70, 70, 70),
        Margin = new Padding(16, 0, 0, 4),
    };

    static Label Note() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(16, 0, 0, 2),
    };

    static FlowLayoutPanel Line(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(13, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    static Button MakeButton(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => click();
        return b;
    }

    // ---------- behaviour ----------

    string FolderId => DriveIds.Parse(_folder.Text);
    bool FolderOk => Regex.IsMatch(FolderId, "^[A-Za-z0-9_-]{15,}$");

    void UpdateState()
    {
        _copy.Enabled = _view.Enabled = FolderOk;
        if (_folder.Text.Trim().Length == 0) _folderNote.Text = "";
        else if (FolderOk) { _folderNote.Text = "✓ Folder link looks right."; _folderNote.ForeColor = Color.SeaGreen; }
        else { _folderNote.Text = "Open a folder (not “My Drive”) and copy its link. It contains /folders/."; _folderNote.ForeColor = Color.Firebrick; }
        _connect.Enabled = _url.Text.Trim().Length > 0;
    }

    string Script()
    {
        using var s = typeof(ConnectForm).Assembly.GetManifestResourceStream("Code.gs")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd()
            .Replace("PASTE_DRIVE_FOLDER_ID_HERE", FolderId)
            .Replace("PASTE_A_LONG_RANDOM_SECRET_HERE", _secret)
            .Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    void CopyScript()
    {
        Clip.SetText(Script());
        // Keep the key now, so it still matches if this window is closed before step 5.
        _settings.AppsScriptSecret = _secret;
        _settings.Save();
        _copied.Text = "✓ Copied. Now do step 3.";
        _copied.ForeColor = Color.SeaGreen;
    }

    void ViewScript()
    {
        using var f = new Form
        {
            Text = "DriveShot upload script",
            Icon = Icon,
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(860, 660),
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = Font,
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            BackColor = Color.White,
            Text = Script(),
        };
        var copy = MakeButton("Copy script", CopyScript);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bar.Controls.Add(copy);
        f.Controls.Add(box);
        f.Controls.Add(bar);
        box.BringToFront();
        f.ShowDialog(this);
    }

    async Task ConnectAsync()
    {
        var url = _url.Text.Trim();
        if (!url.StartsWith("https://script.google.com/", StringComparison.OrdinalIgnoreCase))
        {
            Fail("That isn't an Apps Script web app URL. It starts with https://script.google.com/macros/s/ and ends with /exec.");
            return;
        }
        if (url.EndsWith("/dev", StringComparison.OrdinalIgnoreCase))
        {
            Fail("That is the test URL (/dev). Use the Web app URL that ends with /exec.");
            return;
        }

        _connect.Enabled = false;
        _status.ForeColor = Color.DimGray;
        _status.Text = "Connecting to your Apps Script…";
        try
        {
            var probe = _settings.Clone();
            probe.AppsScriptUrl = url;
            probe.AppsScriptSecret = _secret;
            var (name, id) = await AppsScript.PingAsync(probe, CancellationToken.None);

            _settings.AppsScriptUrl = url;
            _settings.AppsScriptSecret = _secret;
            if (!string.IsNullOrEmpty(id)) _settings.DriveFolder = id;
            else if (FolderOk) _settings.DriveFolder = FolderId;
            _settings.Save();

            _status.ForeColor = Color.SeaGreen;
            _status.Text = $"✓ Connected to the “{name}” folder. You're all set: press PrtSc to take a screenshot.";
            _close.Text = "Done";
            AcceptButton = _close;
            Connected?.Invoke(name);
        }
        catch (Exception ex)
        {
            Fail(Explain(ex.Message));
        }
        finally { _connect.Enabled = _url.Text.Trim().Length > 0; }
    }

    static string Explain(string error)
    {
        if (error.Contains("Wrong secret", StringComparison.OrdinalIgnoreCase))
            return "The script has a different private key. Click Copy script again, paste it over the old code and Save, then " +
                   "Deploy → Manage deployments → pencil icon → Version: New version → Deploy.";
        if (Regex.IsMatch(error, "getFolderById|No item with the given ID", RegexOptions.IgnoreCase))
            return "The script can't open that folder. Check the folder link, and make sure the script was created with the Google account that owns the folder.";
        return error;
    }

    void Fail(string message)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = "✗ " + message;
    }

    static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
