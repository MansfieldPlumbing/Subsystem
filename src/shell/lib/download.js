/* lib/download.js — file download helpers for presenters.
 *
 * Mirrors the Blazor pattern: materialise bytes client-side, hand them to an
 * <a download> with an object URL, click, revoke.
 *
 *   import { downloadText, downloadBase64, shareTextOut } from '/lib/download.js';
 *   downloadText('notes.txt', editor.getValue());
 *   downloadBase64('photo.jpg', b64FromBackend, 'image/jpeg');
 */

export function downloadBlob(fileName, blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName || 'download';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

export function downloadText(fileName, text, mime = 'text/plain;charset=utf-8') {
  downloadBlob(fileName, new Blob([text], { type: mime }));
}

export function downloadBase64(fileName, base64, mime = 'application/octet-stream') {
  const bin = atob(base64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  downloadBlob(fileName, new Blob([bytes], { type: mime }));
}

/* Share text out of the renderer.  Triggers a standard browser download whose
 * data: URL the host's IDownloadListener intercepts in-process — no network,
 * no custom scheme, no JNI.  Returns true so callers know the action fired. */
export function shareTextOut(title, text, mime = 'text/plain') {
  try {
    downloadText(title || 'document', text, mime);
    return true;
  } catch (e) { }
  return false;
}
