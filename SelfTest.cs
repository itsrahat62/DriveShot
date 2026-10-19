using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;

namespace DriveShot;

/// <summary>Developer checks: render the UI to PNGs, and run one real upload without the tray app.</summary>
static class SelfTest
{
    public static int Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        report.AppendLine($"Icon font: {UiFonts.Icon}   Text font: {UiFonts.Text}");
        report.AppendLine($"Virtual screen: {ScreenCapture.VirtualBounds}");
        foreach (var s in Screen.AllScreens)
            report.AppendLine($"Monitor {s.DeviceName}: {s.Bounds} primary={s.Primary} scale={Native.ScaleAt(new Point(s.Bounds.X + 5, s.Bounds.Y + 5))}");
        using (var real = ScreenCapture.Capture(new Rectangle(ScreenCapture.VirtualBounds.Location, new Size(32, 32)), false))
            report.AppendLine($"Real capture pixel alpha: {real.GetPixel(5, 5).A}");

        using var desk = FakeDesktop(1600, 900);
        var settings = new AppSettings();

        using (var ov = new CaptureOverlay((Bitmap)desk.Clone(), settings, new Rectangle(0, 0, 1600, 900), 1.25f))
        {
            ov.TestState(new Rectangle(240, 150, 820, 470), Tool.Arrow, SampleShapes(), "upload", palette: false);
            ov.TestText(new Point(300, 560), "Hello ঢাকা");
            using (var shot = ov.TestRender()) shot.Save(Path.Combine(outDir, "1-overlay.png"), ImageFormat.Png);
            using var result = ov.TestCompose()!;
            result.Save(Path.Combine(outDir, "2-result.png"), ImageFormat.Png);
            report.AppendLine($"Composed result: {result.Width}x{result.Height} {result.PixelFormat}");
        }

        using (var ov = new CaptureOverlay((Bitmap)desk.Clone(), settings, new Rectangle(0, 0, 1600, 900), 1.25f))
        {
            ov.TestState(new Rectangle(880, 400, 720, 500), Tool.Pen, Array.Empty<Shape>(), "color", palette: true);
            using var shot = ov.TestRender();
            shot.Save(Path.Combine(outDir, "3-edge-palette.png"), ImageFormat.Png);
        }

        using (var ov = new CaptureOverlay((Bitmap)desk.Clone(), settings, new Rectangle(0, 0, 1600, 900), 1.25f))
        {
            ov.TestState(new Rectangle(0, 0, 1600, 900), Tool.None, Array.Empty<Shape>(), "pen", palette: false);
            using var shot = ov.TestRender();
            shot.Save(Path.Combine(outDir, "4-fullscreen.png"), ImageFormat.Png);
        }

        using (var t = new Toast())
        using (var thumb = ImageUtil.Thumbnail(desk, 160))
        {
            using (var a = t.Preview(ToastKind.Progress, "Uploading to Google Drive…", "2026-10-19 / Screenshot_2026-10-19_10-45-12.png", thumb, 1.25f))
                a.Save(Path.Combine(outDir, "5-toast-progress.png"), ImageFormat.Png);
            using (var b = t.Preview(ToastKind.Success, "Uploaded — link copied", "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing", thumb, 1.25f, "Open", "Copy link", "Drive folder"))
                b.Save(Path.Combine(outDir, "6-toast-success.png"), ImageFormat.Png);
            using (var c = t.Preview(ToastKind.Error, "Upload failed", "Google sign-in expired. Run in a terminal:  rclone config reconnect gdrive:", thumb, 1.25f, "Retry", "Show file", "Settings"))
                c.Save(Path.Combine(outDir, "7-toast-error.png"), ImageFormat.Png);
        }

        var demo = new AppSettings
        {
            AppsScriptUrl = "https://script.google.com/macros/s/AKfycbExampleDeploymentId/exec",
            AppsScriptSecret = "example-secret",
            DriveFolder = "1AbCdEfGhIjKlMnOpQrStUvWxYz",
        };
        void Snap(Form form, string file)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-10000, -10000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(outDir, file), ImageFormat.Png);
            form.Close();
        }
        using (var form = new SettingsForm(demo, () => { })) Snap(form, "8-settings.png");
        using (var form = new ConnectForm(new AppSettings { DriveFolder = "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz" }))
            Snap(form, "9-connect.png");

        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        return 0;
    }

    public static int Upload(string file, string outFile, string? folderOverride)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var settings = AppSettings.Load();
            if (folderOverride?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true) settings.AppsScriptUrl = folderOverride;
            else if (!string.IsNullOrEmpty(folderOverride)) { settings.DriveFolder = folderOverride; settings.AppsScriptUrl = ""; }
            var now = DateTime.Now;
            var r = Drive.UploadAsync(settings, file, now).GetAwaiter().GetResult();
            var upload = sw.Elapsed.TotalSeconds;
            var folderId = Drive.FindDateFolderIdAsync(settings, now.ToString("yyyy-MM-dd")).GetAwaiter().GetResult();
            File.WriteAllText(outFile, $"OK upload {upload:0.0}s\n{r.Link}\n{r.RemotePath}\ndate folder id: {folderId}\n");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(outFile, $"FAIL after {sw.Elapsed.TotalSeconds:0.0}s\n{ex}\n");
            return 1;
        }
    }

    static IEnumerable<Shape> SampleShapes()
    {
        var pen = new StrokeShape(new PointF(300, 260)) { Color = Color.Red, Width = 3 };
        for (int i = 1; i < 40; i++) pen.Update(new PointF(300 + i * 4, 260 + (float)Math.Sin(i / 4.0) * 18), false);
        yield return pen;
        var marker = new StrokeShape(new PointF(560, 330)) { Color = Color.FromArgb(255, 214, 0), Width = 3, Marker = true };
        marker.Update(new PointF(860, 330), false);
        yield return marker;
        yield return new RectShape { A = new PointF(620, 200), B = new PointF(900, 290), Color = Color.Red, Width = 3 };
        yield return new LineShape { A = new PointF(320, 420), B = new PointF(560, 420), Color = Color.FromArgb(41, 98, 255), Width = 3 };
        yield return new LineShape { A = new PointF(420, 520), B = new PointF(640, 440), Arrow = true, Color = Color.Red, Width = 4 };
    }

    static Bitmap FakeDesktop(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(20, 60, 110), Color.FromArgb(90, 40, 120), 35f))
            g.FillRectangle(bg, 0, 0, w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI", 15, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        void Window(Rectangle r, string caption)
        {
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, r);
            using (var b = new SolidBrush(Color.FromArgb(240, 240, 243))) g.FillRectangle(b, r.X, r.Y, r.Width, 34);
            g.DrawString(caption, title, Brushes.Black, r.X + 12, r.Y + 8);
            for (int i = 0; i < (r.Height - 60) / 26; i++)
                g.DrawString($"Row {i + 1}   Item #{1000 + i}   Status: OK   Total {i + 1}00 ৳", body, Brushes.DimGray, r.X + 16, r.Y + 50 + i * 26);
        }
        Window(new Rectangle(120, 90, 900, 600), "Sample App — Orders");
        Window(new Rectangle(780, 260, 700, 520), "Notes");
        using (var b = new SolidBrush(Color.FromArgb(230, 25, 25, 28))) g.FillRectangle(b, 0, h - 44, w, 44);
        return bmp;
    }
}
