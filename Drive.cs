using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriveShot;

sealed record UploadResult(string Link, string FileId, string RemotePath);

sealed class UploadException(string message) : Exception(message);

/// <summary>
/// Uploads to &lt;Drive folder&gt;/yyyy-MM-dd/&lt;name&gt;, either through a Google Apps Script web app
/// (when its URL is set) or through rclone, reusing the Google sign-in stored in rclone.conf.
/// </summary>
static class Drive
{
    // One upload at a time, so two screenshots taken together can't both create the same date folder.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool UsesAppsScript(AppSettings s) => !string.IsNullOrWhiteSpace(s.AppsScriptUrl);

    public static async Task<UploadResult> UploadAsync(AppSettings s, string localFile, DateTime when, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return UsesAppsScript(s)
                ? await AppsScript.UploadAsync(s, localFile, when, ct)
                : await UploadWithRcloneAsync(s, localFile, when, ct);
        }
        finally { Gate.Release(); }
    }

    static async Task<UploadResult> UploadWithRcloneAsync(AppSettings s, string localFile, DateTime when, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.DriveFolder)) throw new UploadException("Set up the upload in DriveShot Settings first.");
        var rclone = Rclone.Find(s.RclonePath);
        var root = DriveIds.Parse(s.DriveFolder);
        var remotePath = $"{Remote(s)}:{when:yyyy-MM-dd}/{Path.GetFileName(localFile)}";

        await Rclone.RunAsync(rclone, ct, "copyto", localFile, remotePath, "--drive-root-folder-id", root);

        string? id;
        if (s.PublicLink)
        {
            // "rclone link" also grants "anyone with the link can view".
            var output = await Rclone.RunAsync(rclone, ct, "link", remotePath, "--drive-root-folder-id", root);
            id = DriveIds.FromText(output);
        }
        else
        {
            id = await StatIdAsync(rclone, remotePath, root, ct);
        }
        if (string.IsNullOrEmpty(id)) throw new UploadException("Uploaded, but Drive didn't return a file ID.");
        return new UploadResult(DriveIds.FileUrl(id), id, remotePath);
    }

    public static async Task<string?> FindDateFolderIdAsync(AppSettings s, string date, CancellationToken ct = default)
    {
        try
        {
            if (UsesAppsScript(s)) return await AppsScript.FindFolderAsync(s, date, ct);

            // "lsjson --stat" on a folder reports it as the root without an ID, so list the parent instead.
            var json = await Rclone.RunAsync(Rclone.Find(s.RclonePath), ct, "lsjson", $"{Remote(s)}:", "--dirs-only", "--no-modtime", "--no-mimetype",
                "--drive-root-folder-id", DriveIds.Parse(s.DriveFolder));
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.GetProperty("Name").GetString() == date && item.TryGetProperty("ID", out var id))
                    return id.GetString();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Find folder " + date, ex);
            return null;
        }
    }

    /// <summary>Proves the upload settings work. Returns a status line and, when known, the Drive folder ID.</summary>
    public static async Task<(string Message, string? FolderId)> TestAsync(AppSettings s, CancellationToken ct = default)
    {
        if (UsesAppsScript(s))
        {
            var (name, id) = await AppsScript.PingAsync(s, ct);
            return ($"Connected to the Apps Script. Uploads go to the “{name}” folder.", id);
        }
        if (string.IsNullOrWhiteSpace(s.DriveFolder)) throw new UploadException("Enter the Apps Script URL, or the Drive folder for rclone.");
        var output = await Rclone.RunAsync(Rclone.Find(s.RclonePath), ct, "lsf", $"{Remote(s)}:", "--dirs-only", "--drive-root-folder-id", DriveIds.Parse(s.DriveFolder));
        int n = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return ($"Connected with rclone. The Drive folder has {n} sub-folders.", DriveIds.Parse(s.DriveFolder));
    }

    static async Task<string?> StatIdAsync(string rclone, string remotePath, string root, CancellationToken ct)
    {
        var json = await Rclone.RunAsync(rclone, ct, "lsjson", "--stat", remotePath, "--drive-root-folder-id", root);
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ID", out var id) ? id.GetString() : null;
    }

    static string Remote(AppSettings s) => s.RcloneRemote.Trim().TrimEnd(':');
}

/// <summary>Client for the web app in apps-script/Code.gs.</summary>
static class AppsScript
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<UploadResult> UploadAsync(AppSettings s, string localFile, DateTime when, CancellationToken ct)
    {
        var date = when.ToString("yyyy-MM-dd");
        var name = Path.GetFileName(localFile);
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(localFile, ct));
        var r = await CallAsync(s, new { secret = s.AppsScriptSecret, action = "upload", folder = date, name, mimeType = "image/png", data, @public = s.PublicLink }, ct);
        var id = r.TryGetProperty("id", out var v) ? v.GetString() : null;
        if (string.IsNullOrEmpty(id)) throw new UploadException("The Apps Script didn't return a file ID.");
        return new UploadResult(DriveIds.FileUrl(id), id, $"{date}/{name}");
    }

    public static async Task<string?> FindFolderAsync(AppSettings s, string date, CancellationToken ct)
    {
        var r = await CallAsync(s, new { secret = s.AppsScriptSecret, action = "folder", folder = date }, ct);
        return r.TryGetProperty("folderId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static async Task<(string Name, string? Id)> PingAsync(AppSettings s, CancellationToken ct)
    {
        var r = await CallAsync(s, new { secret = s.AppsScriptSecret, action = "ping" }, ct);
        return (r.TryGetProperty("folderName", out var n) ? n.GetString() ?? "" : "",
                r.TryGetProperty("folderId", out var id) ? id.GetString() : null);
    }

    static async Task<JsonElement> CallAsync(AppSettings s, object payload, CancellationToken ct)
    {
        // text/plain keeps Apps Script happy; it reads the body from e.postData.contents.
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "text/plain");
        HttpResponseMessage resp;
        try { resp = await Http.PostAsync(s.AppsScriptUrl.Trim(), content, ct); }
        catch (HttpRequestException ex) { throw new UploadException("Couldn't reach Google: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new UploadException("Google didn't respond in time."); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new UploadException($"The Apps Script returned HTTP {(int)resp.StatusCode}. Check the web app URL.");
            if (text.TrimStart().StartsWith('<'))
                throw new UploadException("The Apps Script didn't answer with data. Deploy it as a web app with access “Anyone”.");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new UploadException("Apps Script: " + (root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error"));
            return root;
        }
    }
}

static class Rclone
{
    public static string Find(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), "rclone.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(local, @"Microsoft\WinGet\Links\rclone.exe"),
            Path.Combine(home, @"scoop\shims\rclone.exe"),
            @"C:\ProgramData\chocolatey\bin\rclone.exe",
            @"C:\Program Files\rclone\rclone.exe",
            @"C:\rclone\rclone.exe",
        };
        var winget = Path.Combine(local, @"Microsoft\WinGet\Packages");
        try
        {
            if (Directory.Exists(winget))
                foreach (var pkg in Directory.GetDirectories(winget, "Rclone.Rclone*"))
                    candidates.AddRange(Directory.GetFiles(pkg, "rclone.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc));
        }
        catch { }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new UploadException("rclone.exe not found. Install rclone or set its path in DriveShot Settings.");
    }

    public static async Task<string> RunAsync(string exe, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--log-level");
        psi.ArgumentList.Add("ERROR");

        using var p = Process.Start(psi) ?? throw new UploadException("Couldn't start rclone.");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new UploadException("Google Drive didn't respond in time.");
        }

        var output = await stdout;
        var errors = await stderr;
        if (p.ExitCode != 0) throw new UploadException(Friendly(errors, p.ExitCode));
        return output;
    }

    static string Friendly(string stderr, int code)
    {
        if (Regex.IsMatch(stderr, @"didn't find section in config|couldn't find section|not found in config", RegexOptions.IgnoreCase))
            return "rclone remote not found. Check the remote name in Settings.";
        if (Regex.IsMatch(stderr, @"invalid_grant|token expired|cannot fetch token|unauthorized_client", RegexOptions.IgnoreCase))
            return "Google sign-in expired. Run in a terminal:  rclone config reconnect <remote>:";
        if (Regex.IsMatch(stderr, @"no such host|dial tcp|connection refused|network is unreachable", RegexOptions.IgnoreCase))
            return "No internet connection.";
        if (Regex.IsMatch(stderr, @"insufficientParentPermissions|insufficientFilePermissions", RegexOptions.IgnoreCase))
            return "This Google account can only view the Drive folder. Share the folder with it as Editor.";
        if (Regex.IsMatch(stderr, @"File not found|notFound", RegexOptions.IgnoreCase))
            return "Drive folder not found. Check the folder link in Settings.";

        var last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(last)) return $"rclone failed (exit code {code}).";
        return Regex.Replace(last, @"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d\s+\w+\s*:\s*", "");
    }
}
