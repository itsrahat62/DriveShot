namespace DriveShot;

static class Program
{
    const string MutexName = @"Local\DriveShot.SingleInstance";
    const string SignalName = @"Local\DriveShot.Capture";

    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length >= 2 && args[0] == "--render-test") return SelfTest.Render(args[1]);
        if (args.Length >= 3 && args[0] == "--upload-test") return SelfTest.Upload(args[1], args[2], args.Length >= 4 ? args[3] : null);

        bool autostart = args.Contains("--autostart");
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Launching DriveShot again while it is running starts a capture, like Lightshot does.
            if (!autostart)
            {
                Native.AllowSetForegroundWindow(-1);
                try { using var ev = EventWaitHandle.OpenExisting(SignalName); ev.Set(); } catch { }
            }
            return 0;
        }

        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal", e.ExceptionObject as Exception);
        Application.Run(new TrayApp(autostart, signal));
        return 0;
    }
}
