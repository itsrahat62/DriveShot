using System.Diagnostics;
using System.Drawing.Imaging;

namespace DriveShot;

sealed class TrayApp : ApplicationContext
{
    const int HkRegion = 1, HkSaveFull = 2, HkUploadFull = 3;

    // Same keys as Lightshot: PrtSc = select area, Shift+PrtSc = save full screen, Ctrl+PrtSc = upload full screen.
    static readonly (int Id, uint Mods, string Label)[] HotkeyDefs =
    {
        (HkRegion, 0, "PrtSc"),
        (HkSaveFull, Native.MOD_SHIFT, "Shift+PrtSc"),
        (HkUploadFull, Native.MOD_CONTROL, "Ctrl+PrtSc"),
    };

    readonly AppSettings _settings = AppSettings.Load();
    readonly History _history = History.Load();
    readonly HotkeyWindow _hotkeys = new();
    readonly Control _ui = new();
    readonly Toast _toast = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _recent = new("Recent uploads");
    CaptureOverlay? _overlay;
    SettingsForm? _settingsForm;
    bool _capturing, _exiting;

    public TrayApp(bool autostart, EventWaitHandle signal)
    {
        _ = _ui.Handle;

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = "DriveShot",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) StartCapture(250); };

        if (_settings.FirstRun)
        {
            Autostart.Set(true);
            _settings.FirstRun = false;
            _settings.Save();
        }
        else if (Autostart.IsEnabled()) Autostart.Set(true); // keep the path current if the exe moved

        _hotkeys.Pressed += OnHotkey;
        var failed = RegisterHotkeys();
        if (failed.Count > 0) ReportHotkeyConflict(failed);
        else if (!autostart) ShowReady();
        if (string.IsNullOrWhiteSpace(_settings.DriveFolder) && !Drive.UsesAppsScript(_settings)) ShowSettings();

        var listener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    signal.WaitOne();
                    if (_exiting) return;
                    _ui.BeginInvoke(new Action(() => StartCapture(0)));
                }
            }
            catch { }
        }) { IsBackground = true, Name = "DriveShot.Signal" };
        listener.Start();
    }

    // ---------- menu ----------

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var take = new ToolStripMenuItem("Take screenshot", null, (_, _) => StartCapture(250)) { ShortcutKeyDisplayString = "PrtSc" };
        take.Font = new Font(take.Font, FontStyle.Bold);
        m.Items.Add(take);
        m.Items.Add(new ToolStripMenuItem("Upload full screen", null, (_, _) => InstantUpload(250)) { ShortcutKeyDisplayString = "Ctrl+PrtSc" });
        m.Items.Add(new ToolStripMenuItem("Save full screen…", null, (_, _) => InstantSave(250)) { ShortcutKeyDisplayString = "Shift+PrtSc" });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(_recent);
        m.Items.Add("Open today's Drive folder", null, (_, _) => OpenDateFolder(DateTime.Now));
        m.Items.Add("Open main Drive folder", null, (_, _) => OpenMainFolder());
        m.Items.Add("Open local copies", null, (_, _) => OpenLocalFolder());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Settings…", null, (_, _) => ShowSettings());
        m.Items.Add("Exit", null, (_, _) => ExitApp());
        m.Opening += (_, _) => RefreshRecent();
        return m;
    }

    void RefreshRecent()
    {
        _recent.DropDownItems.Clear();
        var items = _history.Items.Take(10).ToList();
        if (items.Count == 0)
        {
            _recent.DropDownItems.Add(new ToolStripMenuItem("(no uploads yet)") { Enabled = false });
            return;
        }
        foreach (var h in items)
        {
            var item = new ToolStripMenuItem($"{h.Time:dd MMM  HH:mm:ss}    {h.Name}");
            item.Click += (_, _) =>
            {
                Clip.SetText(h.Link);
                _toast.Present(ToastKind.Success, "Link copied to clipboard", h.Link, ImageUtil.LoadThumbnail(h.LocalPath, 160), 6000,
                    () => Shell.Open(h.Link), ("Open", () => Shell.Open(h.Link)));
            };
            _recent.DropDownItems.Add(item);
        }
    }

    // ---------- hotkeys ----------

    List<string> RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        foreach (var (id, mods, label) in HotkeyDefs)
            if (!_hotkeys.Register(id, mods, Keys.PrintScreen)) failed.Add(label);
        Log.Info(failed.Count == 0 ? "Hotkeys registered: PrtSc, Shift+PrtSc, Ctrl+PrtSc" : "Hotkeys NOT registered: " + string.Join(", ", failed));
        return failed;
    }

    void OnHotkey(int id)
    {
        switch (id)
        {
            case HkRegion: StartCapture(0); break;
            case HkSaveFull: InstantSave(0); break;
            case HkUploadFull: InstantUpload(0); break;
        }
    }

    void ShowReady() =>
        _toast.Present(ToastKind.Info, "DriveShot is running", "Press PrtSc to take a screenshot. Uploads go to Google Drive.", null, 5000);

    void ReportHotkeyConflict(List<string> failed)
    {
        var keys = string.Join(", ", failed);
        if (Process.GetProcessesByName("Lightshot").Length > 0)
        {
            _toast.Present(ToastKind.Error, "PrtSc is being used by Lightshot", $"{keys} will work after Lightshot is closed.", null, 0, null,
                ("Close Lightshot", CloseLightshot), ("Later", _toast.HideToast));
        }
        else
        {
            _toast.Present(ToastKind.Error, "Hotkey already in use", $"{keys} is taken by another app. You can still capture from the tray icon.", null, 15000, null,
                ("Try again", () => { var f = RegisterHotkeys(); if (f.Count == 0) ShowReady(); else ReportHotkeyConflict(f); }));
        }
    }

    void CloseLightshot()
    {
        foreach (var p in Process.GetProcessesByName("Lightshot"))
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Exception ex) { Log.Error("Close Lightshot", ex); }
            finally { p.Dispose(); }
        }
        var failed = RegisterHotkeys();
        if (failed.Count == 0)
            _toast.Present(ToastKind.Success, "DriveShot is ready", "PrtSc now takes screenshots that upload to Google Drive.", null, 5000);
        else ReportHotkeyConflict(failed);
    }

    // ---------- capture ----------

    async Task<Bitmap?> GrabScreenAsync(int delayMs)
    {
        if (_toast.Visible) { _toast.HideToast(); delayMs = Math.Max(delayMs, 150); }
        if (delayMs > 0) await Task.Delay(delayMs);
        try { return ScreenCapture.Capture(ScreenCapture.VirtualBounds, _settings.CaptureCursor); }
        catch (Exception ex)
        {
            Log.Error("Capture", ex);
            _toast.Present(ToastKind.Error, "Couldn't take a screenshot", ex.Message);
            return null;
        }
    }

    async void StartCapture(int delayMs)
    {
        if (_overlay != null) { _overlay.ShowOverlay(); return; }
        if (_capturing) return;
        _capturing = true;
        try
        {
            var vs = ScreenCapture.VirtualBounds;
            var shot = await GrabScreenAsync(delayMs);
            if (shot == null) return;
            var ov = new CaptureOverlay(shot, _settings, vs);
            ov.UploadRequested += img => _ = UploadImageAsync(img);
            ov.CopyRequested += CopyImage;
            ov.FormClosed += (_, _) => { if (_overlay == ov) _overlay = null; _settings.Save(); };
            _overlay = ov;
            ov.ShowOverlay();
        }
        catch (Exception ex)
        {
            Log.Error("Overlay", ex);
            _toast.Present(ToastKind.Error, "Couldn't open the capture screen", ex.Message);
        }
        finally { _capturing = false; }
    }

    async void InstantUpload(int delayMs)
    {
        using var shot = await GrabScreenAsync(delayMs);
        if (shot == null) return;
        await UploadImageAsync(ImageUtil.To24(shot));
    }

    async void InstantSave(int delayMs)
    {
        using var shot = await GrabScreenAsync(delayMs);
        if (shot == null) return;
        using var img = ImageUtil.To24(shot);
        using var owner = DialogOwner();
        SaveHelper.SaveWithDialog(owner, img, _settings);
    }

    /// <summary>Invisible top-most window so dialogs opened from the tray appear in front.</summary>
    static Form DialogOwner()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(area.X + area.Width / 2, area.Y + area.Height / 3, 1, 1),
            Opacity = 0,
        };
        f.Show();
        f.Activate();
        return f;
    }

    void CopyImage(Bitmap img)
    {
        try
        {
            Clip.SetImage(img);
            _toast.Present(ToastKind.Info, "Screenshot copied", $"{img.Width} x {img.Height} image is on the clipboard.", ImageUtil.Thumbnail(img, 160), 2500);
        }
        catch (Exception ex)
        {
            Log.Error("Copy", ex);
            _toast.Present(ToastKind.Error, "Couldn't copy the screenshot", ex.Message);
        }
        finally { img.Dispose(); }
    }

    // ---------- upload ----------

    async Task UploadImageAsync(Bitmap image)
    {
        var when = DateTime.Now;
        string local;
        Image thumb;
        try
        {
            local = SaveLocalCopy(image, when);
            thumb = ImageUtil.Thumbnail(image, 160);
        }
        catch (Exception ex)
        {
            Log.Error("Save local copy", ex);
            _toast.Present(ToastKind.Error, "Couldn't save the screenshot", ex.Message);
            return;
        }
        finally { image.Dispose(); }
        await UploadFileAsync(local, when, thumb);
    }

    async Task UploadFileAsync(string local, DateTime when, Image thumb)
    {
        var name = Path.GetFileName(local);
        _toast.Present(ToastKind.Progress, "Uploading to Google Drive…", $"{when:yyyy-MM-dd} / {name}", thumb, 0);
        _tray.Text = "DriveShot – uploading…";
        try
        {
            var result = await Drive.UploadAsync(_settings, local, when);
            if (_settings.CopyLink) Clip.SetText(result.Link);
            _history.Add(new HistoryEntry { Time = when, Name = name, Link = result.Link, LocalPath = local });
            _toast.Present(ToastKind.Success, _settings.CopyLink ? "Uploaded — link copied" : "Uploaded to Google Drive", result.Link, thumb, 10000,
                () => Shell.Open(result.Link),
                ("Open", () => Shell.Open(result.Link)),
                ("Copy link", () => Clip.SetText(result.Link)),
                ("Drive folder", () => OpenDateFolder(when)));
        }
        catch (Exception ex)
        {
            Log.Error("Upload " + name, ex);
            _toast.Present(ToastKind.Error, "Upload failed", ex.Message.Replace("<remote>", _settings.RcloneRemote), thumb, 0, null,
                ("Retry", () => _ = UploadFileAsync(local, when, thumb)),
                ("Show file", () => Shell.ShowInExplorer(local)),
                ("Settings", ShowSettings));
        }
        finally { _tray.Text = "DriveShot"; }
    }

    string SaveLocalCopy(Bitmap img, DateTime when)
    {
        var dir = Path.Combine(_settings.LocalFolder, when.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var stem = $"Screenshot_{when:yyyy-MM-dd_HH-mm-ss}";
        var path = Path.Combine(dir, stem + ".png");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem}_{n}.png");
        img.Save(path, ImageFormat.Png);
        return path;
    }

    async void OpenDateFolder(DateTime when)
    {
        var date = when.ToString("yyyy-MM-dd");
        var id = await Drive.FindDateFolderIdAsync(_settings, date);
        if (id != null) { Shell.Open(DriveIds.FolderUrl(id)); return; }
        _toast.Present(ToastKind.Info, $"No {date} folder yet", "Opening the main Drive folder instead.", null, 4000);
        OpenMainFolder();
    }

    void OpenMainFolder()
    {
        if (string.IsNullOrWhiteSpace(_settings.DriveFolder)) { ShowSettings(); return; }
        Shell.Open(DriveIds.FolderUrl(DriveIds.Parse(_settings.DriveFolder)));
    }

    void OpenLocalFolder()
    {
        Directory.CreateDirectory(_settings.LocalFolder);
        Shell.Open(_settings.LocalFolder);
    }

    void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(_settings);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _overlay?.Close();
        _settingsForm?.Close();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _toast.Close();
        _settings.Save();
        ExitThread();
    }
}
