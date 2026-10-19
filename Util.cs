using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriveShot;

static class UiFonts
{
    public static readonly string Icon = Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    // Nirmala UI has Latin and Bengali glyphs, so text typed with Avro renders correctly.
    public static readonly string Text = Has("Nirmala UI") ? "Nirmala UI" : "Segoe UI";
    public static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));

    static bool Has(string name)
    {
        using var f = new Font(name, 10f);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public static SizeF Measure(string s, Font f) => Measurer.MeasureString(s, f);
}

static class Gfx
{
    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        using var path = RoundRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void DrawRound(Graphics g, Rectangle r, int radius, Color c)
    {
        using var path = RoundRect(r, radius);
        using var p = new Pen(c);
        g.DrawPath(p, path);
    }

    public static void Center(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, sf);
    }
}

static class ImageUtil
{
    public static Bitmap Crop24(Bitmap src, Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return bmp;
    }

    public static Bitmap To24(Bitmap src) => Crop24(src, new Rectangle(Point.Empty, src.Size));

    public static Bitmap Thumbnail(Image src, int max)
    {
        float k = Math.Min(1f, Math.Min((float)max / src.Width, (float)max / src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        return bmp;
    }

    public static Image? LoadThumbnail(string path, int max)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return Thumbnail(img, max);
        }
        catch { return null; }
    }

    public static void Save(Image img, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ps = new EncoderParameters(1))
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                    img.Save(path, codec, ps);
                }
                break;
            case ".bmp": img.Save(path, ImageFormat.Bmp); break;
            default: img.Save(path, ImageFormat.Png); break;
        }
    }
}

static class Clip
{
    public static void SetText(string text)
    {
        try { Clipboard.SetDataObject(text, true, 10, 100); }
        catch (Exception ex) { Log.Error("Clipboard text", ex); }
    }

    public static void SetImage(Bitmap img)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, img);
        var png = new MemoryStream();
        img.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        Clipboard.SetDataObject(data, true, 10, 100);
    }
}

static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open " + target, ex); }
    }

    public static void ShowInExplorer(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (Exception ex) { Log.Error("Explorer", ex); }
    }
}

static class SaveHelper
{
    static readonly Regex Numbered = new(@"^Screenshot_(\d+)$", RegexOptions.IgnoreCase);

    public static string DefaultDir(AppSettings s)
    {
        if (Directory.Exists(s.LastSaveDir)) return s.LastSaveDir;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\SkillBrains\Lightshot");
            if (k?.GetValue("LastSavedDir") is string d && Directory.Exists(d)) return d;
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    /// <summary>Lightshot-style "Screenshot_N" with the next free number in the folder.</summary>
    public static string NextName(string dir)
    {
        int max = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "Screenshot_*"))
            {
                var m = Numbered.Match(Path.GetFileNameWithoutExtension(f));
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) max = Math.Max(max, n);
            }
        }
        catch { }
        return $"Screenshot_{max + 1}";
    }

    public static bool SaveWithDialog(IWin32Window? owner, Image img, AppSettings s)
    {
        var dir = DefaultDir(s);
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp",
            InitialDirectory = dir,
            FileName = NextName(dir),
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
            Title = "Save screenshot",
        };
        var result = owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
        if (result != DialogResult.OK) return false;
        try
        {
            ImageUtil.Save(img, dlg.FileName);
            s.LastSaveDir = Path.GetDirectoryName(dlg.FileName) ?? s.LastSaveDir;
            s.Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Couldn't save screenshot", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

static class AppIcon
{
    public static Icon Load(Size size)
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s, size) : (Icon)SystemIcons.Application.Clone();
    }
}
