using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DriveShot;

enum ToastKind { Info, Progress, Success, Error }

/// <summary>Small dark popup in the bottom-right corner (upload progress, link, errors).</summary>
sealed class Toast : Form
{
    sealed class Btn { public string Text = ""; public Action? Click; public Rectangle Bounds; }

    readonly List<Btn> _buttons = new();
    readonly System.Windows.Forms.Timer _close = new();
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 30 };
    ToastKind _kind;
    string _title = "", _message = "";
    Image? _thumb;
    Action? _bodyClick;
    float _s = 1, _fontScale;
    int _phase, _autoCloseMs;
    bool _mouseInside, _hoverX;
    Btn? _hoverBtn;
    Rectangle _xRect, _thumbRect, _titleRect, _msgRect;
    Font? _titleFont, _msgFont, _btnFont, _iconFont;

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(34, 34, 36);
        Text = "DriveShot";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _close.Tick += (_, _) => { if (_mouseInside) return; HideToast(); };
        _anim.Tick += (_, _) => { _phase = (_phase + 1) % 100; Invalidate(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    public void Present(ToastKind kind, string title, string message, Image? thumb = null, int autoCloseMs = 6000,
        Action? bodyClick = null, params (string Text, Action Click)[] buttons)
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Configure(kind, title, message, thumb, bodyClick, buttons, Native.ScaleAt(new Point(wa.Right - 20, wa.Bottom - 20)));
        var size = DoLayout();
        Bounds = new Rectangle(wa.Right - size.Width - S(16), wa.Bottom - size.Height - S(16), size.Width, size.Height);

        if (kind == ToastKind.Progress) _anim.Start(); else _anim.Stop();
        _autoCloseMs = autoCloseMs;
        _close.Stop();
        if (autoCloseMs > 0) { _close.Interval = autoCloseMs; _close.Start(); }
        if (!Visible) Show();
        Invalidate();
    }

    public void HideToast()
    {
        _anim.Stop();
        _close.Stop();
        Hide();
    }

    void Configure(ToastKind kind, string title, string message, Image? thumb, Action? bodyClick, (string Text, Action Click)[] buttons, float scale)
    {
        _kind = kind;
        _title = title;
        _message = message;
        _thumb = thumb;
        _bodyClick = bodyClick;
        _hoverBtn = null;
        _buttons.Clear();
        foreach (var (text, click) in buttons) _buttons.Add(new Btn { Text = text, Click = click });
        _s = scale;
        if (_titleFont == null || _fontScale != _s)
        {
            _titleFont?.Dispose(); _msgFont?.Dispose(); _btnFont?.Dispose(); _iconFont?.Dispose();
            _titleFont = new Font("Segoe UI Semibold", 14.5f * _s, GraphicsUnit.Pixel);
            _msgFont = new Font("Segoe UI", 12.5f * _s, GraphicsUnit.Pixel);
            _btnFont = new Font("Segoe UI", 12.5f * _s, GraphicsUnit.Pixel);
            _iconFont = new Font(UiFonts.Icon, 10f * _s, GraphicsUnit.Pixel);
            _fontScale = _s;
        }
    }

    int S(float v) => (int)Math.Round(v * _s);

    Size DoLayout()
    {
        int w = S(400), pad = S(14);
        int x = pad + S(4);
        if (_thumb != null)
        {
            _thumbRect = new Rectangle(x, pad + S(2), S(76), S(56));
            x += _thumbRect.Width + S(12);
        }
        else _thumbRect = Rectangle.Empty;

        _xRect = new Rectangle(w - S(30), S(8), S(22), S(22));
        int titleH = (int)Math.Ceiling(_titleFont!.GetHeight(UiFonts.Measurer));
        int lineH = (int)Math.Ceiling(_msgFont!.GetHeight(UiFonts.Measurer));
        int msgLines = _kind == ToastKind.Error ? 2 : 1;
        _titleRect = new Rectangle(x, pad, _xRect.Left - x - S(4), titleH);
        _msgRect = new Rectangle(x, _titleRect.Bottom + S(3), w - x - pad, lineH * msgLines);

        int y = _msgRect.Bottom;
        if (_buttons.Count > 0)
        {
            int bx = x, by = y + S(10), bh = S(28);
            foreach (var b in _buttons)
            {
                int bw = (int)Math.Ceiling(UiFonts.Measure(b.Text, _btnFont!).Width) + S(22);
                b.Bounds = new Rectangle(bx, by, bw, bh);
                bx += bw + S(8);
            }
            y = by + bh;
        }
        int h = y + pad;
        if (_kind == ToastKind.Progress) h += S(4);
        if (!_thumbRect.IsEmpty) h = Math.Max(h, _thumbRect.Bottom + pad);
        return new Size(w, h);
    }

    Color Accent => _kind switch
    {
        ToastKind.Success => Color.FromArgb(34, 197, 94),
        ToastKind.Error => Color.FromArgb(239, 68, 68),
        _ => Color.FromArgb(59, 130, 246),
    };

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ClientSize);

    void Render(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        using (var border = new Pen(Color.FromArgb(70, 70, 74))) g.DrawRectangle(border, 0, 0, size.Width - 1, size.Height - 1);
        using (var accent = new SolidBrush(Accent)) g.FillRectangle(accent, 0, 0, S(4), size.Height);

        if (_thumb != null && !_thumbRect.IsEmpty)
        {
            float k = Math.Min((float)_thumbRect.Width / _thumb.Width, (float)_thumbRect.Height / _thumb.Height);
            float tw = _thumb.Width * k, th = _thumb.Height * k;
            var dst = new RectangleF(_thumbRect.X + (_thumbRect.Width - tw) / 2, _thumbRect.Y + (_thumbRect.Height - th) / 2, tw, th);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_thumb, dst);
            using var p = new Pen(Color.FromArgb(80, 80, 84));
            g.DrawRectangle(p, dst.X, dst.Y, dst.Width, dst.Height);
        }

        using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using (var b = new SolidBrush(Color.White)) g.DrawString(_title, _titleFont!, b, _titleRect, sf);

        bool isLink = _message.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        using (var msgFormat = new StringFormat { Trimming = isLink ? StringTrimming.EllipsisPath : StringTrimming.EllipsisWord })
        {
            if (_kind != ToastKind.Error) msgFormat.FormatFlags = StringFormatFlags.NoWrap;
            using var b = new SolidBrush(isLink ? Color.FromArgb(138, 180, 248) : Color.FromArgb(205, 205, 210));
            g.DrawString(_message, _msgFont!, b, _msgRect, msgFormat);
        }

        foreach (var btn in _buttons)
        {
            Gfx.FillRound(g, btn.Bounds, S(4), btn == _hoverBtn ? Color.FromArgb(78, 78, 84) : Color.FromArgb(56, 56, 60));
            Gfx.Center(g, btn.Text, _btnFont!, Color.White, btn.Bounds);
        }

        if (_hoverX) Gfx.FillRound(g, _xRect, S(4), Color.FromArgb(60, 60, 64));
        Gfx.Center(g, "", _iconFont!, Color.FromArgb(190, 190, 195), _xRect);

        if (_kind == ToastKind.Progress)
        {
            int barH = S(3), y = size.Height - barH;
            using (var track = new SolidBrush(Color.FromArgb(55, 55, 60))) g.FillRectangle(track, 0, y, size.Width, barH);
            int segW = size.Width / 3;
            int x = (int)((size.Width + segW) * (_phase / 100f)) - segW;
            using var seg = new SolidBrush(Accent);
            g.FillRectangle(seg, x, y, segW, barH);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouseInside = true;
        var hb = _buttons.FirstOrDefault(b => b.Bounds.Contains(e.Location));
        bool hx = _xRect.Contains(e.Location);
        if (hb != _hoverBtn || hx != _hoverX) { _hoverBtn = hb; _hoverX = hx; Invalidate(); }
        Cursor = hb != null || hx || _bodyClick != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        _hoverBtn = null;
        _hoverX = false;
        Invalidate();
        if (_autoCloseMs > 0) { _close.Stop(); _close.Interval = 2500; _close.Start(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_xRect.Contains(e.Location)) { HideToast(); return; }
        var b = _buttons.FirstOrDefault(x => x.Bounds.Contains(e.Location));
        if (b != null) { b.Click?.Invoke(); return; }
        _bodyClick?.Invoke();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    internal Bitmap Preview(ToastKind kind, string title, string message, Image? thumb, float scale, params string[] buttons)
    {
        Configure(kind, title, message, thumb, null, buttons.Select(b => (b, (Action)(() => { }))).ToArray(), scale);
        var size = DoLayout();
        _phase = 40;
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        Render(g, size);
        return bmp;
    }
}
