using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriveShot;

sealed class AppSettings
{
    public string AppsScriptUrl { get; set; } = "";
    public string AppsScriptSecret { get; set; } = "";
    public string RcloneRemote { get; set; } = "gdrive";
    public string DriveFolder { get; set; } = "";
    public string RclonePath { get; set; } = "";
    public string LocalFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DriveShot");
    public bool PublicLink { get; set; } = true;
    public bool CopyLink { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    public string LastSaveDir { get; set; } = "";
    public int ColorArgb { get; set; } = unchecked((int)0xFFFF0000);
    public float PenWidth { get; set; } = 3;
    public bool FirstRun { get; set; } = true;

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveShot");
    static string FilePath => Path.Combine(Dir, "settings.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Error("Settings load", ex); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("Settings save", ex); }
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
    public string LocalPath { get; set; } = "";
}

sealed class History
{
    public List<HistoryEntry> Items { get; set; } = new();
    static string FilePath => Path.Combine(AppSettings.Dir, "history.json");

    public static History Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<History>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Error("History load", ex); }
        return new();
    }

    public void Add(HistoryEntry entry)
    {
        Items.Insert(0, entry);
        if (Items.Count > 200) Items.RemoveRange(200, Items.Count - 200);
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettings.Json));
        }
        catch (Exception ex) { Log.Error("History save", ex); }
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write($"{message}");
    public static void Error(string where, Exception? ex) => Write($"[{where}] {ex}");

    static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.AppendAllText(Path.Combine(AppSettings.Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n");
            }
        }
        catch { }
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "DriveShot";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --autostart");
            else k.DeleteValue(Name, false);
        }
        catch (Exception ex) { Log.Error("Autostart", ex); }
    }
}

static class DriveIds
{
    static readonly Regex IdInUrl = new(@"(?:folders/|/d/|[?&]id=)([A-Za-z0-9_-]{10,})", RegexOptions.Compiled);

    /// <summary>Accepts a bare ID or any Drive file/folder URL and returns the ID.</summary>
    public static string Parse(string input)
    {
        input = input.Trim();
        var m = IdInUrl.Match(input);
        return m.Success ? m.Groups[1].Value : input;
    }

    public static string? FromText(string text)
    {
        var m = IdInUrl.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string FolderUrl(string id) => $"https://drive.google.com/drive/folders/{id}";
    public static string FileUrl(string id) => $"https://drive.google.com/file/d/{id}/view?usp=sharing";
}
