/**
 * DriveShot — Google Drive upload script (Google Apps Script web app)
 *
 * Saves each screenshot into  <your folder>/<yyyy-MM-dd>/<name>.png  in the Drive of the
 * Google account that deploys this script, and returns a shareable link.
 *
 * EASIEST: use DriveShot's tray menu -> "Connect Google Drive…". It fills in the two
 * lines below for you and walks you through every click.
 *
 * MANUAL SETUP
 *   1. Open https://script.google.com with the Google account that owns the Drive folder
 *      and click "New project".
 *   2. Delete everything in the editor and paste this whole file.
 *   3. Change ONLY the two lines marked "CHANGE THIS" below:
 *        FOLDER_ID  Open your Drive folder. The ID is the last part of the address:
 *                   https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz
 *                                                          └──────── FOLDER_ID ───────┘
 *        SECRET     Any long random text, like a password. You type the same text in DriveShot.
 *      Click Save (disk icon).
 *   4. Deploy -> New deployment -> gear icon next to "Select type" -> Web app
 *        Execute as: Me        Who has access: Anyone
 *      Click Deploy -> Authorize access -> choose your account.
 *      If Google says "Google hasn't verified this app": Advanced -> Go to ... (unsafe) -> Allow.
 *      (It is your own script; it only touches the folder you set.)
 *   5. Copy the "Web app URL" (ends with /exec).
 *   6. DriveShot tray icon -> Settings -> paste the Web app URL and the same SECRET -> Test connection -> Save.
 *
 * If you edit this code later: Deploy -> Manage deployments -> pencil icon -> Version: New version -> Deploy.
 */

const FOLDER_ID = 'PASTE_DRIVE_FOLDER_ID_HERE';       // <-- CHANGE THIS: your Drive folder ID
const SECRET = 'PASTE_A_LONG_RANDOM_SECRET_HERE';     // <-- CHANGE THIS: any long random text

// Nothing below this line needs to be changed.

function doPost(e) {
  try {
    const req = JSON.parse(e.postData.contents);
    if (req.secret !== SECRET) return reply({ ok: false, error: 'Wrong secret.' });

    const root = DriveApp.getFolderById(FOLDER_ID);
    switch (req.action) {
      case 'ping':
        return reply({ ok: true, folderName: root.getName(), folderId: root.getId() });

      case 'folder': {
        const it = root.getFoldersByName(req.folder);
        return reply({ ok: true, folderId: it.hasNext() ? it.next().getId() : null });
      }

      case 'upload': {
        const folder = dateFolder(root, req.folder);
        const blob = Utilities.newBlob(Utilities.base64Decode(req.data), req.mimeType || 'image/png', req.name);
        const file = folder.createFile(blob);
        if (req.public) file.setSharing(DriveApp.Access.ANYONE_WITH_LINK, DriveApp.Permission.VIEW);
        return reply({
          ok: true,
          id: file.getId(),
          url: 'https://drive.google.com/file/d/' + file.getId() + '/view?usp=sharing',
          folderId: folder.getId(),
        });
      }

      default:
        return reply({ ok: false, error: 'Unknown action: ' + req.action });
    }
  } catch (err) {
    return reply({ ok: false, error: String(err && err.message || err) });
  }
}

/** Finds or creates the dated sub-folder; the lock stops two uploads from creating it twice. */
function dateFolder(root, name) {
  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const it = root.getFoldersByName(name);
    return it.hasNext() ? it.next() : root.createFolder(name);
  } finally {
    lock.releaseLock();
  }
}

function reply(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
