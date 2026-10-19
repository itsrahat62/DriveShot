namespace DriveShot;

/// <summary>Message-only window that receives global hotkeys.</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action<int>? Pressed;
    readonly List<int> _ids = new();

    public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

    public bool Register(int id, uint modifiers, Keys key)
    {
        bool ok = Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)key);
        if (ok) _ids.Add(id);
        return ok;
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
        _ids.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
