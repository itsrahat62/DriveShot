using System.Drawing.Drawing2D;
using System.Text;

namespace DriveShot;

abstract class Shape
{
    public Color Color;
    public float Width = 3;
    public abstract void Draw(Graphics g);
    public virtual void Update(PointF p, bool shift) { }
    public virtual bool IsEmpty => false;

    protected static PointF Snap45(PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new PointF(a.X + (float)(Math.Cos(ang) * len), a.Y + (float)(Math.Sin(ang) * len));
    }
}

/// <summary>Freehand pen, or a translucent wide marker.</summary>
sealed class StrokeShape : Shape
{
    public bool Marker;
    readonly List<PointF> _pts = new();

    public StrokeShape(PointF start) => _pts.Add(start);

    public override void Update(PointF p, bool shift)
    {
        var last = _pts[^1];
        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 1) _pts.Add(p);
    }

    public override void Draw(Graphics g)
    {
        var color = Marker ? Color.FromArgb(110, Color) : Color;
        float w = Marker ? Width * 4 + 6 : Width;
        if (_pts.Count == 1)
        {
            using var b = new SolidBrush(color);
            g.FillEllipse(b, _pts[0].X - w / 2, _pts[0].Y - w / 2, w, w);
            return;
        }
        using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        path.AddLines(_pts.ToArray());
        g.DrawPath(pen, path);
    }
}

sealed class LineShape : Shape
{
    public bool Arrow;
    public PointF A, B;

    public override void Update(PointF p, bool shift) => B = shift ? Snap45(A, p) : p;
    public override bool IsEmpty => Math.Abs(A.X - B.X) + Math.Abs(A.Y - B.Y) < 3;

    public override void Draw(Graphics g)
    {
        using var pen = new Pen(Color, Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (!Arrow) { g.DrawLine(pen, A, B); return; }

        float dx = B.X - A.X, dy = B.Y - A.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        float ux = dx / len, uy = dy / len;
        float head = Math.Min(len, Math.Max(12f, Width * 3.2f + 8)), half = head * 0.5f;
        var basePt = new PointF(B.X - ux * head, B.Y - uy * head);
        g.DrawLine(pen, A, new PointF(B.X - ux * head * 0.7f, B.Y - uy * head * 0.7f));
        using var brush = new SolidBrush(Color);
        g.FillPolygon(brush, new[]
        {
            B,
            new PointF(basePt.X - uy * half, basePt.Y + ux * half),
            new PointF(basePt.X + uy * half, basePt.Y - ux * half),
        });
    }
}

sealed class RectShape : Shape
{
    public PointF A, B;

    public override void Update(PointF p, bool shift)
    {
        if (shift)
        {
            float side = Math.Max(Math.Abs(p.X - A.X), Math.Abs(p.Y - A.Y));
            p = new PointF(A.X + Math.Sign(p.X - A.X) * side, A.Y + Math.Sign(p.Y - A.Y) * side);
        }
        B = p;
    }

    public override bool IsEmpty => Math.Abs(A.X - B.X) < 2 || Math.Abs(A.Y - B.Y) < 2;

    public override void Draw(Graphics g)
    {
        using var pen = new Pen(Color, Width) { LineJoin = LineJoin.Miter };
        g.DrawRectangle(pen, Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Abs(A.X - B.X), Math.Abs(A.Y - B.Y));
    }
}

sealed class TextShape : Shape
{
    public PointF Pos;
    public float SizePx = 20;
    readonly StringBuilder _text = new();
    static readonly StringFormat Format = CreateFormat();

    static StringFormat CreateFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
        return f;
    }

    public override bool IsEmpty => _text.ToString().Trim().Length == 0;
    public void Append(string s) => _text.Append(s);

    public void Backspace()
    {
        if (_text.Length == 0) return;
        int n = _text.Length >= 2 && char.IsLowSurrogate(_text[^1]) && char.IsHighSurrogate(_text[^2]) ? 2 : 1;
        _text.Remove(_text.Length - n, n);
    }

    Font MakeFont() => new(UiFonts.Text, SizePx, FontStyle.Bold, GraphicsUnit.Pixel);

    public override void Draw(Graphics g)
    {
        if (_text.Length == 0) return;
        using var f = MakeFont();
        using var b = new SolidBrush(Color);
        g.DrawString(_text.ToString(), f, b, Pos, Format);
    }

    public RectangleF EditBox()
    {
        using var f = MakeFont();
        var lines = _text.ToString().Split('\n');
        float lh = f.GetHeight(UiFonts.Measurer);
        float w = SizePx;
        foreach (var l in lines) w = Math.Max(w, UiFonts.Measurer.MeasureString(l, f, PointF.Empty, Format).Width);
        float pad = Math.Max(4, SizePx * 0.2f);
        return new RectangleF(Pos.X - pad, Pos.Y - pad, w + pad * 2 + 2, lh * lines.Length + pad * 2);
    }

    public void DrawEditing(Graphics g, bool caretOn)
    {
        Draw(g);
        var box = EditBox();
        using (var dark = new Pen(Color.FromArgb(150, 0, 0, 0)))
            g.DrawRectangle(dark, box.X, box.Y, box.Width, box.Height);
        using (var dash = new Pen(Color.FromArgb(230, 255, 255, 255)) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(dash, box.X, box.Y, box.Width, box.Height);
        if (!caretOn) return;

        using var f = MakeFont();
        var lines = _text.ToString().Split('\n');
        float lh = f.GetHeight(UiFonts.Measurer);
        var last = lines[^1];
        float x = Pos.X + (last.Length == 0 ? 0 : UiFonts.Measurer.MeasureString(last, f, PointF.Empty, Format).Width) + 1;
        float y = Pos.Y + lh * (lines.Length - 1);
        using var caret = new Pen(Color, Math.Max(1.5f, SizePx / 14f));
        g.DrawLine(caret, x, y + lh * 0.1f, x, y + lh * 0.92f);
    }
}
