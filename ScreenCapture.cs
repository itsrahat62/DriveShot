using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DriveShot;

static class ScreenCapture
{
    /// <summary>All monitors, in physical pixels (the process is per-monitor DPI aware).</summary>
    public static Rectangle VirtualBounds => SystemInformation.VirtualScreen;

    public static Bitmap Capture(Rectangle bounds, bool includeCursor)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        if (includeCursor) DrawCursor(g, bounds.Location);
        return bmp;
    }

    static void DrawCursor(Graphics g, Point origin)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;

        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out var info))
        {
            hx = info.xHotspot;
            hy = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
        }

        var hdc = g.GetHdc();
        try { Native.DrawIconEx(hdc, ci.ptScreenPos.X - origin.X - hx, ci.ptScreenPos.Y - origin.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */); }
        finally { g.ReleaseHdc(hdc); }
    }
}
