# DriveShot

A Lightshot-style screenshot tool for Windows that uploads straight to **Google Drive**.
Press <kbd>PrtSc</kbd>, select an area, annotate it, and click **Upload** — the image lands in a
dated sub-folder (`yyyy-MM-dd`) of your Drive folder and the share link is copied to your clipboard.

## Download

**[DriveShot.exe](https://github.com/itsrahat62/DriveShot/releases/latest/download/DriveShot.exe)** — Windows 10/11 x64, nothing else to install.

Run it once: it sits in the tray, adds itself to the Start Menu and to Windows startup, and opens Settings so you
can connect Google Drive (see below). Windows SmartScreen may warn because the exe isn't code-signed —
choose *More info → Run anyway*.

![Capture screen](docs/capture.png)

![Upload finished](docs/uploaded.png)

## Features

- **Area capture** across all monitors (per-monitor DPI aware), with resize handles, arrow-key nudging and a live size label.
- **Annotations:** pen, line, arrow, rectangle, marker, text (Bangla works too), colour palette, line width with the mouse wheel, undo.
  Hold <kbd>Shift</kbd> for straight lines and squares.
- **Actions:** upload to Drive, copy, save (PNG/JPG/BMP), print.
- **Upload popup** with a thumbnail, the link, and *Open / Copy link / Drive folder* buttons. Failed uploads can be retried.
- A local copy of every upload is kept in `Pictures\DriveShot\yyyy-MM-dd\`.
- Tray icon with recent uploads, today's Drive folder, settings, and *Start with Windows*.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| <kbd>PrtSc</kbd> | Select an area |
| <kbd>Ctrl</kbd>+<kbd>PrtSc</kbd> | Upload the full screen immediately |
| <kbd>Shift</kbd>+<kbd>PrtSc</kbd> | Save the full screen |
| <kbd>Ctrl</kbd>+<kbd>D</kbd> / <kbd>C</kbd> / <kbd>S</kbd> / <kbd>P</kbd> | Upload / copy / save / print the selection |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> | Undo |
| <kbd>Ctrl</kbd>+<kbd>A</kbd> | Select the whole monitor |
| <kbd>P</kbd> <kbd>L</kbd> <kbd>A</kbd> <kbd>R</kbd> <kbd>M</kbd> <kbd>T</kbd> | Pen, line, arrow, rectangle, marker, text |
| Mouse wheel | Line width / text size |
| Right-click | Clear the selection (again to close) |
| <kbd>Esc</kbd> | Close |

If Lightshot is running it owns <kbd>PrtSc</kbd>; DriveShot offers to close it. Turn off Lightshot in
*Task Manager → Startup apps* so the two don't race at sign-in.

## Connecting Google Drive

DriveShot can upload in two ways. Pick one in **tray icon → Settings**.

### Option 1 — Google Apps Script (recommended)

Fast (a couple of seconds), no rate limits, nothing to install.

1. Open <https://script.google.com> with the Google account that owns the Drive folder.
2. **New project**, paste [`apps-script/Code.gs`](apps-script/Code.gs), and fill in `FOLDER_ID`
   (the part after `/folders/` in the folder's URL) and a long random `SECRET`.
3. **Deploy → New deployment → Web app**, set *Execute as: Me* and *Who has access: Anyone*,
   deploy, and allow the permissions.
4. Paste the **Web app URL** and the same secret into DriveShot Settings and click **Test connection**.

### Option 2 — rclone

Uses an existing [rclone](https://rclone.org) Google Drive remote. Leave the Apps Script URL empty and fill in
the remote name and the Drive folder link. The signed-in account needs **Editor** access to the folder.
rclone's shared Google client is heavily rate-limited, so uploads can take 10–20 seconds;
[using your own client ID](https://rclone.org/drive/#making-your-own-client-id) fixes that.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
dotnet publish DriveShot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o App
.\App\DriveShot.exe
```

Settings, upload history and the log live in `%APPDATA%\DriveShot\`.

Developer checks: `DriveShot.exe --render-test <folder>` renders the UI to PNGs, and
`DriveShot.exe --upload-test <image> <result.txt> [apps-script-url | folder-id]` runs one real upload.
