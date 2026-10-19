using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Drawing.Text;

namespace DriveShot;

enum Tool { None, Pen, Line, Arrow, Rect, Marker, Text }

/// <summary>
/// Full-screen frozen screenshot with Lightshot-style area selection, annotation tools
/// and the two toolbars (tools on the side, actions underneath).
/// </summary>
sealed class CaptureOverlay : Form
{
    public event Action<Bitmap>? UploadRequested;
    public event Action<Bitmap>? CopyRequested;

    sealed class UiButton
    {
        public string Id = "", Tip = "";
        public Tool Tool;
        public Action? Click;
        public bool Vertical, GapBefore;
        public Rectangle Bounds;
    }

    enum DragMode { None, Select, Move, Resize, Draw }

    static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(255, 128, 0), Color.FromArgb(255, 214, 0), Color.FromArgb(0, 200, 83),
        Color.FromArgb(0, 184, 212), Color.FromArgb(41, 98, 255), Color.FromArgb(170, 0, 255), Color.FromArgb(255, 64, 160),
        Color.FromArgb(255, 255, 255), Color.FromArgb(190, 190, 190), Color.FromArgb(110, 110, 110), Color.FromArgb(0, 0, 0),
        Color.FromArgb(150, 0, 0), Color.FromArgb(120, 70, 20), Color.FromArgb(0, 100, 40), Color.FromArgb(10, 30, 120),
    };

    readonly Bitmap _shot, _dim;
    readonly AppSettings _settings;
    readonly Rectangle _screen;          // screen area covered by this window (client 0,0 = _screen.Location)
    readonly float? _fixedScale;         // self-test only
    readonly List<UiButton> _buttons = new();
    readonly List<Shape> _shapes = new();
    readonly List<Rectangle> _swatches = new();
    readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 530 };
    readonly System.Windows.Forms.Timer _hintTimer = new() { Interval = 900 };

    Rectangle _sel, _prevSel;
    bool _hasSel, _prevHasSel;
    DragMode _drag;
    int _handle = -1;
    Point _down, _mouse;
    Rectangle _selAtDown;
    Tool _tool;
    Shape? _current;
    TextShape? _editing;
    Color _color;
    float _width, _s = 1f, _fontScale;
    Rectangle _vbar, _hbar, _palette, _more, _tip, _lastDirty;
    string _tipText = "";
    UiButton? _hover;
    int _hoverSwatch = -1;
    bool _hoverMore, _paletteOpen, _caretOn = true, _showSizeHint;
    Font? _iconFont, _uiFont, _glyphFont;

    public CaptureOverlay(Bitmap shot, AppSettings settings, Rectangle screen, float? fixedScale = null)
    {
        _shot = shot;
        _settings = settings;
        _screen = screen;
        _fixedScale = fixedScale;
        _color = Color.FromArgb(settings.ColorArgb);
        _width = Math.Clamp(settings.PenWidth, 1, 40);

        _dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_dim))
        {
            var all = new Rectangle(Point.Empty, shot.Size);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(shot, all, all, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            using var veil = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillRectangle(veil, all);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Text = "DriveShot";
        Cursor = Cursors.Cross;
        Bounds = screen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        BuildButtons();
        _caretTimer.Tick += (_, _) => { _caretOn = !_caretOn; if (_editing != null) Invalidate(Rectangle.Inflate(EditRect(), 4, 4)); };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _showSizeHint = false; Repaint(); };
    }

    public void ShowOverlay()
    {
        if (!Visible) Show();
        Bounds = _screen;
        // Paint the dimmed screenshot now, before any queued mouse input is handled.
        Refresh();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    void BuildButtons()
    {
        void V(string id, string tip, Tool t = Tool.None, Action? a = null) =>
            _buttons.Add(new UiButton { Id = id, Tip = tip, Tool = t, Click = a, Vertical = true });
        void H(string id, string tip, Action a, bool gap = false) =>
            _buttons.Add(new UiButton { Id = id, Tip = tip, Click = a, GapBefore = gap });

        V("pen", "Pen  (P)", Tool.Pen);
        V("line", "Line  (L)", Tool.Line);
        V("arrow", "Arrow  (A)", Tool.Arrow);
        V("rect", "Rectangle  (R)", Tool.Rect);
        V("marker", "Marker  (M)", Tool.Marker);
        V("text", "Text  (T)", Tool.Text);
        V("color", "Color", a: TogglePalette);
        V("undo", "Undo  (Ctrl+Z)", a: Undo);
        H("upload", "Upload to Google Drive  (Ctrl+D)", DoUpload);
        H("print", "Print  (Ctrl+P)", DoPrint);
        H("copy", "Copy  (Ctrl+C)", DoCopy);
        H("save", "Save  (Ctrl+S)", DoSave);
        H("close", "Close  (Esc)", Close, gap: true);
    }

    // ---------- geometry ----------

    int S(float v) => (int)Math.Round(v * _s);
    static int Clamp(int v, int min, int max) => max < min ? min : Math.Min(Math.Max(v, min), max);

    Point ScreenCenter(Rectangle r) => new(_screen.X + r.X + r.Width / 2, _screen.Y + r.Y + r.Height / 2);

    Rectangle MonitorBounds(Rectangle clientRect)
    {
        if (_fixedScale.HasValue) return new Rectangle(Point.Empty, _screen.Size);
        var b = Screen.FromPoint(ScreenCenter(clientRect)).Bounds;
        b.Offset(-_screen.X, -_screen.Y);
        return b;
    }

    void UpdateScale()
    {
        _s = _fixedScale ?? Native.ScaleAt(ScreenCenter(_hasSel && _sel.Width > 0 ? _sel : new Rectangle(_mouse, new Size(1, 1))));
        if (_iconFont != null && _fontScale == _s) return;
        _iconFont?.Dispose(); _uiFont?.Dispose(); _glyphFont?.Dispose();
        _iconFont = new Font(UiFonts.Icon, 16 * _s, GraphicsUnit.Pixel);
        _uiFont = new Font("Segoe UI", 12 * _s, GraphicsUnit.Pixel);
        _glyphFont = new Font("Segoe UI", 18 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontScale = _s;
    }

    Font UiFont { get { if (_uiFont == null) UpdateScale(); return _uiFont!; } }

    void LayoutBars()
    {
        _vbar = _hbar = Rectangle.Empty;
        UpdateScale();
        if (!_hasSel || _drag == DragMode.Select) { _paletteOpen = false; return; }

        var mon = MonitorBounds(_sel);
        int b = S(30), pad = S(3), gap = S(6), sep = S(9);
        var vb = _buttons.Where(x => x.Vertical).ToList();
        var hb = _buttons.Where(x => !x.Vertical).ToList();

        // Tools: right of the selection, bottom-aligned (left side or inside when there is no room).
        int vw = b + pad * 2, vh = vb.Count * b + pad * 2;
        int vx = _sel.Right + gap;
        if (vx + vw > mon.Right) { vx = _sel.Left - gap - vw; if (vx < mon.Left) vx = _sel.Right - gap - vw; }
        int vy = Clamp(_sel.Bottom - vh, mon.Top + gap, mon.Bottom - vh - gap);
        _vbar = new Rectangle(vx, vy, vw, vh);

        // Actions: below the selection, right-aligned (above or inside when there is no room).
        int hw = hb.Count * b + hb.Count(x => x.GapBefore) * sep + pad * 2, hh = b + pad * 2;
        int hy = _sel.Bottom + gap;
        if (hy + hh > mon.Bottom) { hy = _sel.Top - gap - hh; if (hy < mon.Top) hy = _sel.Bottom - gap - hh; }
        int hx = Clamp(_sel.Right - hw, mon.Left + gap, mon.Right - hw - gap);
        _hbar = new Rectangle(hx, hy, hw, hh);
        if (_hbar.IntersectsWith(_vbar))
        {
            int alt = _vbar.Left - gap - hw;
            if (alt >= mon.Left + gap) _hbar.X = alt;
        }

        int y = _vbar.Y + pad;
        foreach (var x in vb) { x.Bounds = new Rectangle(_vbar.X + pad, y, b, b); y += b; }
        int xx = _hbar.X + pad;
        foreach (var x in hb) { if (x.GapBefore) xx += sep; x.Bounds = new Rectangle(xx, _hbar.Y + pad, b, b); xx += b; }

        if (_paletteOpen) LayoutPalette(mon);
    }

    void LayoutPalette(Rectangle mon)
    {
        var colorBtn = _buttons.First(x => x.Id == "color");
        int sw = S(22), g = S(5), pad = S(9), cols = 4, rows = (Palette.Length + cols - 1) / cols, moreH = S(26);
        int pw = pad * 2 + cols * sw + (cols - 1) * g;
        int ph = pad * 2 + rows * sw + (rows - 1) * g + g + moreH;
        int px = _vbar.Left - S(6) - pw;
        if (px < mon.Left) px = _vbar.Right + S(6);
        int py = Clamp(colorBtn.Bounds.Top - S(10), mon.Top + S(6), mon.Bottom - ph - S(6));
        _palette = new Rectangle(px, py, pw, ph);
        _swatches.Clear();
        for (int i = 0; i < Palette.Length; i++)
            _swatches.Add(new Rectangle(px + pad + i % cols * (sw + g), py + pad + i / cols * (sw + g), sw, sw));
        _more = new Rectangle(px + pad, py + pad + rows * (sw + g), pw - pad * 2, moreH);
    }

    static Point[] HandlePoints(Rectangle r)
    {
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
            new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy),
        };
    }

    int HitHandle(Point p)
    {
        if (!_hasSel || _sel.Width == 0 || _drag == DragMode.Select) return -1;
        int t = S(7);
        var pts = HandlePoints(_sel);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= t && Math.Abs(p.Y - pts[i].Y) <= t) return i;
        if (_tool != Tool.None) return -1;

        int e = S(4);
        bool inX = p.X > _sel.Left - e && p.X < _sel.Right + e, inY = p.Y > _sel.Top - e && p.Y < _sel.Bottom + e;
        if (inY && Math.Abs(p.X - _sel.Left) <= e) return 7;
        if (inY && Math.Abs(p.X - _sel.Right) <= e) return 3;
        if (inX && Math.Abs(p.Y - _sel.Top) <= e) return 1;
        if (inX && Math.Abs(p.Y - _sel.Bottom) <= e) return 5;
        return -1;
    }

    static Rectangle ResizeRect(Rectangle r, int h, int dx, int dy)
    {
        int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (h is 0 or 6 or 7) l += dx;
        if (h is 2 or 3 or 4) rt += dx;
        if (h is 0 or 1 or 2) t += dy;
        if (h is 4 or 5 or 6) b += dy;
        return Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b));
    }

    UiButton? HitButton(Point p) => _vbar.IsEmpty ? null : _buttons.FirstOrDefault(x => x.Bounds.Contains(p));

    Rectangle EditRect() => _editing == null ? Rectangle.Empty : Rectangle.Ceiling(_editing.EditBox());

    static float TextSize(float w) => 12 + w * 3;

    // ---------- mouse ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = e.Location;
        _mouse = p;
        if (e.Button == MouseButtons.Right) { OnRightClick(); return; }
        if (e.Button != MouseButtons.Left) return;

        if (_paletteOpen)
        {
            _paletteOpen = false;
            int i = _swatches.FindIndex(r => r.Contains(p));
            if (i >= 0) { SetColor(Palette[i]); Repaint(); return; }
            if (_more.Contains(p)) { Repaint(); PickCustomColor(); return; }
            Repaint();
            if (_palette.Contains(p) || HitButton(p)?.Id == "color") return;
        }

        var btn = HitButton(p);
        if (btn != null) { ClickButton(btn); return; }

        if (_editing != null)
        {
            if (EditRect().Contains(p)) return;
            CommitText();
            if (_tool == Tool.Text && _sel.Contains(p)) { StartText(p); return; }
            Repaint();
        }

        if (_hasSel)
        {
            int h = HitHandle(p);
            if (h >= 0) { _drag = DragMode.Resize; _handle = h; _down = p; _selAtDown = _sel; return; }
            if (_sel.Contains(p))
            {
                switch (_tool)
                {
                    case Tool.None: _drag = DragMode.Move; _down = p; _selAtDown = _sel; break;
                    case Tool.Text: StartText(p); break;
                    default: _drag = DragMode.Draw; _current = NewShape(p); Repaint(); break;
                }
                return;
            }
        }

        // Start a new selection (the old one comes back if this turns out to be a plain click).
        _prevSel = _sel;
        _prevHasSel = _hasSel;
        _drag = DragMode.Select;
        _down = p;
        _sel = new Rectangle(p, Size.Empty);
        _hasSel = true;
        _hover = null;
        UpdateTip();
        LayoutBars();
        Repaint();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.Location;
        _mouse = p;
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        switch (_drag)
        {
            case DragMode.Select:
                _sel = Rectangle.FromLTRB(Math.Min(_down.X, p.X), Math.Min(_down.Y, p.Y), Math.Max(_down.X, p.X), Math.Max(_down.Y, p.Y));
                UpdateScale();
                Repaint();
                return;
            case DragMode.Move:
                var r = _selAtDown;
                r.Offset(p.X - _down.X, p.Y - _down.Y);
                r.X = Clamp(r.X, 0, _screen.Width - r.Width);
                r.Y = Clamp(r.Y, 0, _screen.Height - r.Height);
                _sel = r;
                LayoutBars();
                Repaint();
                return;
            case DragMode.Resize:
                _sel = Rectangle.Intersect(ResizeRect(_selAtDown, _handle, p.X - _down.X, p.Y - _down.Y), new Rectangle(Point.Empty, _screen.Size));
                LayoutBars();
                Repaint();
                return;
            case DragMode.Draw:
                _current?.Update(p, shift);
                Repaint();
                return;
        }
        UpdateHover(p);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (_drag)
        {
            case DragMode.Select:
                if (_sel.Width < 3 || _sel.Height < 3) { _sel = _prevSel; _hasSel = _prevHasSel; }
                break;
            case DragMode.Draw:
                if (_current is { IsEmpty: false }) _shapes.Add(_current);
                _current = null;
                break;
        }
        _drag = DragMode.None;
        LayoutBars();
        UpdateHover(e.Location, force: true);
        Repaint();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_hasSel) return;
        _width = Math.Clamp(_width + (e.Delta > 0 ? 1 : -1), 1, 40);
        _settings.PenWidth = _width;
        if (_editing != null) _editing.SizePx = TextSize(_width);
        _showSizeHint = true;
        _hintTimer.Stop();
        _hintTimer.Start();
        Repaint();
    }

    void OnRightClick()
    {
        if (_editing != null) { CommitText(); Repaint(); return; }
        if (_paletteOpen) { _paletteOpen = false; Repaint(); return; }
        if (_hasSel)
        {
            _hasSel = false;
            _sel = Rectangle.Empty;
            _shapes.Clear();
            _tool = Tool.None;
            _hover = null;
            UpdateTip();
            LayoutBars();
            Repaint();
            Cursor = Cursors.Cross;
            return;
        }
        Close();
    }

    void UpdateHover(Point p, bool force = false)
    {
        var btn = HitButton(p);
        int sw = _paletteOpen ? _swatches.FindIndex(r => r.Contains(p)) : -1;
        bool more = _paletteOpen && _more.Contains(p);
        if (force || btn != _hover || sw != _hoverSwatch || more != _hoverMore)
        {
            _hover = btn;
            _hoverSwatch = sw;
            _hoverMore = more;
            UpdateTip();
            Repaint();
        }
        Cursor = PickCursor(p, btn != null || sw >= 0 || more);
    }

    Cursor PickCursor(Point p, bool overUi)
    {
        if (overUi || (_paletteOpen && _palette.Contains(p))) return Cursors.Hand;
        if (_editing != null && EditRect().Contains(p)) return Cursors.IBeam;
        int h = HitHandle(p);
        if (h >= 0) return h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, _ => Cursors.SizeWE };
        if (_hasSel && _sel.Contains(p))
            return _tool switch { Tool.None => Cursors.SizeAll, Tool.Text => Cursors.IBeam, _ => Cursors.Cross };
        return Cursors.Cross;
    }

    // ---------- keyboard ----------

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editing != null)
        {
            if (e.KeyCode == Keys.Escape) { CommitText(); Repaint(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.V) { PasteText(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Back) { _editing.Backspace(); _caretOn = true; Repaint(); e.SuppressKeyPress = true; return; }
            if (!e.Control) return; // characters arrive through OnKeyPress
        }

        if (e.Control)
        {
            switch (e.KeyCode)
            {
                case Keys.Z: Undo(); break;
                case Keys.C: DoCopy(); break;
                case Keys.S: DoSave(); break;
                case Keys.D: DoUpload(); break;
                case Keys.P: DoPrint(); break;
                case Keys.A: SelectMonitor(); break;
                default: return;
            }
            e.SuppressKeyPress = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (_paletteOpen) { _paletteOpen = false; Repaint(); }
                else Close();
                break;
            case Keys.Left: Nudge(-1, 0, e.Shift); break;
            case Keys.Right: Nudge(1, 0, e.Shift); break;
            case Keys.Up: Nudge(0, -1, e.Shift); break;
            case Keys.Down: Nudge(0, 1, e.Shift); break;
            case Keys.P: SelectTool(Tool.Pen); break;
            case Keys.L: SelectTool(Tool.Line); break;
            case Keys.A: SelectTool(Tool.Arrow); break;
            case Keys.R: SelectTool(Tool.Rect); break;
            case Keys.M: SelectTool(Tool.Marker); break;
            case Keys.T: SelectTool(Tool.Text); break;
            default: return;
        }
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_editing == null) return;
        if (e.KeyChar == '\r') _editing.Append("\n");
        else if (!char.IsControl(e.KeyChar)) _editing.Append(e.KeyChar.ToString());
        else return;
        e.Handled = true;
        _caretOn = true;
        Repaint();
    }

    void Nudge(int dx, int dy, bool resize)
    {
        if (!_hasSel) return;
        if (resize)
        {
            _sel.Width = Math.Max(1, _sel.Width + dx);
            _sel.Height = Math.Max(1, _sel.Height + dy);
        }
        else
        {
            _sel.X = Clamp(_sel.X + dx, 0, _screen.Width - _sel.Width);
            _sel.Y = Clamp(_sel.Y + dy, 0, _screen.Height - _sel.Height);
        }
        LayoutBars();
        Repaint();
    }

    void SelectMonitor()
    {
        CommitText();
        var cursor = PointToClient(Cursor.Position);
        _sel = Rectangle.Intersect(MonitorBounds(new Rectangle(cursor, new Size(1, 1))), new Rectangle(Point.Empty, _screen.Size));
        _hasSel = true;
        LayoutBars();
        Repaint();
    }

    // ---------- tools ----------

    void SelectTool(Tool t)
    {
        if (!_hasSel) return;
        CommitText();
        _tool = _tool == t ? Tool.None : t;
        _paletteOpen = false;
        Repaint();
    }

    void ClickButton(UiButton b)
    {
        if (b.Tool != Tool.None) SelectTool(b.Tool);
        else b.Click?.Invoke();
    }

    Shape NewShape(Point p) => _tool switch
    {
        Tool.Pen => new StrokeShape(p) { Color = _color, Width = _width },
        Tool.Marker => new StrokeShape(p) { Color = _color, Width = _width, Marker = true },
        Tool.Line => new LineShape { A = p, B = p, Color = _color, Width = _width },
        Tool.Arrow => new LineShape { A = p, B = p, Arrow = true, Color = _color, Width = _width },
        _ => new RectShape { A = p, B = p, Color = _color, Width = _width },
    };

    void StartText(Point p)
    {
        float size = TextSize(_width);
        _editing = new TextShape { Color = _color, Width = _width, SizePx = size, Pos = new PointF(p.X, p.Y - size * 0.6f) };
        _caretOn = true;
        _caretTimer.Start();
        Repaint();
    }

    void CommitText()
    {
        if (_editing == null) return;
        if (!_editing.IsEmpty) _shapes.Add(_editing);
        _editing = null;
        _caretTimer.Stop();
    }

    void PasteText()
    {
        try
        {
            if (_editing != null && Clipboard.ContainsText())
            {
                _editing.Append(Clipboard.GetText().Replace("\r\n", "\n"));
                Repaint();
            }
        }
        catch { }
    }

    void Undo()
    {
        if (_editing != null) { _editing = null; _caretTimer.Stop(); }
        else if (_shapes.Count > 0) _shapes.RemoveAt(_shapes.Count - 1);
        Repaint();
    }

    void TogglePalette()
    {
        _paletteOpen = !_paletteOpen;
        if (_paletteOpen) LayoutPalette(MonitorBounds(_sel));
        UpdateTip();
        Repaint();
    }

    void SetColor(Color c)
    {
        _color = c;
        _settings.ColorArgb = c.ToArgb();
        if (_editing != null) _editing.Color = c;
    }

    void PickCustomColor()
    {
        using var dlg = new ColorDialog { Color = _color, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetColor(dlg.Color);
        Repaint();
    }

    // ---------- actions ----------

    Bitmap? Compose()
    {
        CommitText();
        if (!_hasSel) return null;
        var r = Rectangle.Intersect(_sel, new Rectangle(Point.Empty, _shot.Size));
        if (r.Width < 1 || r.Height < 1) return null;

        var bmp = ImageUtil.Crop24(_shot, r);
        using var g = Graphics.FromImage(bmp);
        g.TranslateTransform(-r.X, -r.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        foreach (var s in _shapes) s.Draw(g);
        return bmp;
    }

    void DoUpload()
    {
        var img = Compose();
        if (img == null) return;
        Hide();
        UploadRequested?.Invoke(img);
        Close();
    }

    void DoCopy()
    {
        var img = Compose();
        if (img == null) return;
        Hide();
        CopyRequested?.Invoke(img);
        Close();
    }

    void DoSave()
    {
        using var img = Compose();
        if (img == null) return;
        Repaint();
        if (SaveHelper.SaveWithDialog(this, img, _settings)) Close();
    }

    void DoPrint()
    {
        using var img = Compose();
        if (img == null) return;
        Repaint();
        using var doc = new PrintDocument { DocumentName = "Screenshot" };
        doc.PrintPage += (_, e) =>
        {
            var m = e.MarginBounds;
            float w = img.Width * 100f / 96f, h = img.Height * 100f / 96f; // pixels -> 1/100 inch
            float k = Math.Min(1f, Math.Min(m.Width / w, m.Height / h));
            e.Graphics!.DrawImage(img, new RectangleF(m.X, m.Y, w * k, h * k));
        };
        using var dlg = new PrintDialog { Document = doc, UseEXDialog = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { doc.Print(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Print failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        Close();
    }

    // ---------- painting ----------

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, e.ClipRectangle);

    void Repaint()
    {
        var now = ComputeDirty();
        var area = _lastDirty.IsEmpty ? now : now.IsEmpty ? _lastDirty : Rectangle.Union(_lastDirty, now);
        if (!area.IsEmpty) Invalidate(area);
        _lastDirty = now;
    }

    Rectangle ComputeDirty()
    {
        if (!_hasSel) return Rectangle.Empty;
        var r = Rectangle.Inflate(_sel, S(12), S(12));
        r = Rectangle.Union(r, Rectangle.Inflate(LabelRect(), 2, 2));
        if (!_vbar.IsEmpty)
        {
            r = Rectangle.Union(r, Rectangle.Inflate(_vbar, S(5), S(5)));
            r = Rectangle.Union(r, Rectangle.Inflate(_hbar, S(5), S(5)));
        }
        if (_paletteOpen) r = Rectangle.Union(r, Rectangle.Inflate(_palette, S(5), S(5)));
        if (!_tip.IsEmpty) r = Rectangle.Union(r, Rectangle.Inflate(_tip, 2, 2));
        if (_showSizeHint) r = Rectangle.Union(r, SizeHintRect());
        if (_editing != null) r = Rectangle.Union(r, Rectangle.Inflate(EditRect(), 4, 4));
        return r;
    }

    void Render(Graphics g, Rectangle clip)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_dim, clip, clip, GraphicsUnit.Pixel);
        if (_hasSel && _sel.Width > 0 && _sel.Height > 0)
        {
            var bright = Rectangle.Intersect(_sel, clip);
            if (!bright.IsEmpty) g.DrawImage(_shot, bright, bright, GraphicsUnit.Pixel);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        if (!_hasSel) return;

        var state = g.Save();
        g.SetClip(_sel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        foreach (var s in _shapes) s.Draw(g);
        _current?.Draw(g);
        _editing?.DrawEditing(g, _caretOn);
        g.Restore(state);

        DrawFrame(g);
        DrawSizeLabel(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (!_vbar.IsEmpty)
        {
            DrawBar(g, _vbar, vertical: true);
            DrawBar(g, _hbar, vertical: false);
        }
        if (_paletteOpen) DrawPalette(g);
        if (_showSizeHint) DrawSizeHint(g);
        if (!_tip.IsEmpty) DrawTip(g);
    }

    void DrawFrame(Graphics g)
    {
        if (_sel.Width <= 0 || _sel.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.None;
        var r = new Rectangle(_sel.X - 1, _sel.Y - 1, _sel.Width + 1, _sel.Height + 1);
        using (var dark = new Pen(Color.FromArgb(110, 0, 0, 0))) g.DrawRectangle(dark, r);
        using (var dash = new Pen(Color.White) { DashPattern = new float[] { 4, 3 } }) g.DrawRectangle(dash, r);
        if (_drag == DragMode.Select) return;

        int hs = Math.Max(5, S(6));
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(70, 70, 70));
        foreach (var p in HandlePoints(_sel))
        {
            var hr = new Rectangle(p.X - hs / 2, p.Y - hs / 2, hs, hs);
            g.FillRectangle(fill, hr);
            g.DrawRectangle(border, hr);
        }
    }

    string LabelText => $"{_sel.Width} x {_sel.Height}";

    Rectangle LabelRect()
    {
        if (!_hasSel) return Rectangle.Empty;
        var sz = UiFonts.Measure(LabelText, UiFont);
        int w = (int)Math.Ceiling(sz.Width) + S(10), h = (int)Math.Ceiling(sz.Height) + S(4);
        int x = _sel.X, y = _sel.Y - h - S(5);
        if (y < MonitorBounds(_sel).Top) { x = _sel.X + S(5); y = _sel.Y + S(5); }
        return new Rectangle(x, y, w, h);
    }

    void DrawSizeLabel(Graphics g)
    {
        if (_sel.Width <= 0 || _sel.Height <= 0) return;
        var r = LabelRect();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Gfx.FillRound(g, r, S(3), Color.FromArgb(200, 20, 20, 20));
        Gfx.Center(g, LabelText, UiFont, Color.White, r);
    }

    void DrawBar(Graphics g, Rectangle bar, bool vertical)
    {
        int rad = S(5);
        Gfx.FillRound(g, new Rectangle(bar.X - 1, bar.Y, bar.Width + 2, bar.Height + S(3)), rad + 1, Color.FromArgb(45, 0, 0, 0));
        Gfx.FillRound(g, bar, rad, Color.FromArgb(250, 250, 250));
        Gfx.DrawRound(g, bar, rad, Color.FromArgb(205, 205, 205));

        foreach (var b in _buttons.Where(x => x.Vertical == vertical))
        {
            bool active = (b.Tool != Tool.None && b.Tool == _tool) || (b.Id == "color" && _paletteOpen);
            if (active || b == _hover)
                Gfx.FillRound(g, Rectangle.Inflate(b.Bounds, -S(2), -S(2)), S(4), active ? Color.FromArgb(210, 225, 255) : Color.FromArgb(232, 232, 232));
            if (b.GapBefore)
            {
                using var sepPen = new Pen(Color.FromArgb(215, 215, 215));
                int sx = b.Bounds.X - S(9) / 2 - 1;
                g.DrawLine(sepPen, sx, b.Bounds.Y + S(5), sx, b.Bounds.Bottom - S(5));
            }
            DrawIcon(g, b);
        }
    }

    void DrawIcon(Graphics g, UiButton b)
    {
        var r = b.Bounds;
        var fg = Color.FromArgb(45, 45, 45);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = 8 * _s;
        using var pen = new Pen(fg, 1.7f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (b.Id)
        {
            case "line":
                g.DrawLine(pen, cx - k * 0.8f, cy + k * 0.8f, cx + k * 0.8f, cy - k * 0.8f);
                break;
            case "arrow":
                g.DrawLine(pen, cx - k * 0.8f, cy + k * 0.8f, cx + k * 0.45f, cy - k * 0.45f);
                using (var br = new SolidBrush(fg))
                    g.FillPolygon(br, new[] { new PointF(cx + k * 0.9f, cy - k * 0.9f), new PointF(cx + k * 0.05f, cy - k * 0.7f), new PointF(cx + k * 0.7f, cy - k * 0.05f) });
                break;
            case "rect":
                g.DrawRectangle(pen, cx - k * 0.85f, cy - k * 0.65f, k * 1.7f, k * 1.3f);
                break;
            case "text":
                Gfx.Center(g, "T", _glyphFont!, fg, r);
                break;
            case "color":
                using (var br = new SolidBrush(_color)) g.FillEllipse(br, cx - k * 0.75f, cy - k * 0.75f, k * 1.5f, k * 1.5f);
                using (var ring = new Pen(Color.FromArgb(120, 0, 0, 0))) g.DrawEllipse(ring, cx - k * 0.75f, cy - k * 0.75f, k * 1.5f, k * 1.5f);
                break;
            default:
                Gfx.Center(g, Glyph(b.Id), _iconFont!, b.Id == "upload" ? Color.FromArgb(26, 115, 232) : fg, r);
                break;
        }
    }

    static string Glyph(string id) => id switch
    {
        "pen" => "",
        "marker" => "",
        "undo" => "",
        "upload" => "",
        "print" => "",
        "copy" => "",
        "save" => "",
        "close" => "",
        _ => "?",
    };

    void DrawPalette(Graphics g)
    {
        Gfx.FillRound(g, new Rectangle(_palette.X - 1, _palette.Y, _palette.Width + 2, _palette.Height + S(3)), S(6), Color.FromArgb(45, 0, 0, 0));
        Gfx.FillRound(g, _palette, S(5), Color.FromArgb(250, 250, 250));
        Gfx.DrawRound(g, _palette, S(5), Color.FromArgb(205, 205, 205));
        for (int i = 0; i < _swatches.Count; i++)
        {
            var r = _swatches[i];
            using (var b = new SolidBrush(Palette[i])) g.FillRectangle(b, r);
            using (var p = new Pen(i == _hoverSwatch ? Color.FromArgb(90, 90, 90) : Color.FromArgb(200, 200, 200))) g.DrawRectangle(p, r);
            if (Palette[i].ToArgb() == _color.ToArgb())
            {
                using var sel = new Pen(Color.FromArgb(26, 115, 232), 2f * _s);
                g.DrawRectangle(sel, Rectangle.Inflate(r, S(2), S(2)));
            }
        }
        if (_hoverMore) Gfx.FillRound(g, _more, S(4), Color.FromArgb(232, 232, 232));
        Gfx.Center(g, "More colors…", UiFont, Color.FromArgb(50, 50, 50), _more);
    }

    void UpdateTip()
    {
        _tip = Rectangle.Empty;
        _tipText = "";
        if (_hover == null || _drag != DragMode.None || _paletteOpen) return;
        _tipText = _hover.Tip;
        var sz = UiFonts.Measure(_tipText, UiFont);
        int w = (int)Math.Ceiling(sz.Width) + S(14), h = (int)Math.Ceiling(sz.Height) + S(8);
        var mon = MonitorBounds(_sel);
        var b = _hover.Bounds;
        int x, y;
        if (_hover.Vertical)
        {
            x = _vbar.Right + S(6);
            if (x + w > mon.Right) x = _vbar.Left - S(6) - w;
            y = b.Y + (b.Height - h) / 2;
        }
        else
        {
            y = _hbar.Bottom + S(6);
            if (y + h > mon.Bottom) y = _hbar.Top - S(6) - h;
            x = Clamp(b.X + (b.Width - w) / 2, mon.Left + 2, mon.Right - w - 2);
        }
        _tip = new Rectangle(x, y, w, h);
    }

    void DrawTip(Graphics g)
    {
        Gfx.FillRound(g, _tip, S(4), Color.FromArgb(235, 30, 30, 30));
        Gfx.Center(g, _tipText, UiFont, Color.White, _tip);
    }

    float StrokeSize => _tool == Tool.Marker ? _width * 4 + 6 : _width;

    Rectangle SizeHintRect()
    {
        int r = (int)StrokeSize / 2 + S(70);
        return new Rectangle(_mouse.X - r, _mouse.Y - r, r * 2, r * 2);
    }

    void DrawSizeHint(Graphics g)
    {
        float d = StrokeSize;
        var c = _tool == Tool.Marker ? Color.FromArgb(110, _color) : _color;
        using (var b = new SolidBrush(c)) g.FillEllipse(b, _mouse.X - d / 2, _mouse.Y - d / 2, d, d);
        using (var p = new Pen(Color.White)) g.DrawEllipse(p, _mouse.X - d / 2 - 1, _mouse.Y - d / 2 - 1, d + 2, d + 2);
        var label = _tool == Tool.Text || _editing != null ? $"Text {TextSize(_width):0}px" : $"Size {_width:0}";
        var sz = UiFonts.Measure(label, UiFont);
        var r = new Rectangle(_mouse.X + (int)(d / 2) + S(8), _mouse.Y - (int)sz.Height / 2 - S(3), (int)sz.Width + S(12), (int)sz.Height + S(6));
        Gfx.FillRound(g, r, S(3), Color.FromArgb(220, 20, 20, 20));
        Gfx.Center(g, label, UiFont, Color.White, r);
    }

    // ---------- window plumbing ----------

    protected override void WndProc(ref Message m)
    {
        // The overlay spans every monitor at physical pixels; don't let WinForms rescale it.
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _caretTimer.Dispose();
        _hintTimer.Dispose();
        _shot.Dispose();
        _dim.Dispose();
        _iconFont?.Dispose();
        _uiFont?.Dispose();
        _glyphFont?.Dispose();
    }

    // ---------- self-test hooks ----------

    internal void TestState(Rectangle sel, Tool tool, IEnumerable<Shape> shapes, string? hoverId, bool palette)
    {
        _sel = sel;
        _hasSel = true;
        _tool = tool;
        _shapes.Clear();
        _shapes.AddRange(shapes);
        _paletteOpen = palette;
        LayoutBars();
        _hover = hoverId == null ? null : _buttons.First(b => b.Id == hoverId);
        UpdateTip();
    }

    internal void TestText(Point p, string text)
    {
        StartText(p);
        _editing!.Append(text);
    }

    internal Bitmap TestRender()
    {
        var bmp = new Bitmap(_shot.Width, _shot.Height);
        using var g = Graphics.FromImage(bmp);
        Render(g, new Rectangle(Point.Empty, _shot.Size));
        return bmp;
    }

    internal Bitmap? TestCompose() => Compose();
}
