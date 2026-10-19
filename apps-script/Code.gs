/**
 * DriveShot upload endpoint (Google Apps Script web app).
 *
 * Saves each screenshot into  <FOLDER_ID>/<yyyy-MM-dd>/<name>.png  in the Drive of the
 * account that deploys this script, and returns a shareable link.
 *
 * Setup:
 *   1. Open https://script.google.com with the Google account that owns the Drive folder.
 *   2. New project -> paste this file -> fill in FOLDER_ID and SECRET below -> Save.
 *   3. Deploy -> New deployment -> type "Web app"
 *        Execute as: Me      Who has access: Anyone
 *      Deploy, allow the permissions, and copy the Web app URL.
 *   4. In DriveShot: tray icon -> Settings -> paste the URL and the same SECRET.
 */

const FOLDER_ID = 'PASTE_DRIVE_FOLDER_ID_HERE';
const SECRET = 'PASTE_A_LONG_RANDOM_SECRET_HERE';

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
