/* lib/api.js — the unified API helper for WS client and cmdlet execution.
 *
 * Implements a single reusable WebSocketClient class featuring auto-reconnection
 * with backoff, and an executeCommand function to call C# backend seams.
 */

export class WebSocketClient {
  constructor(urlPath, options = {}) {
    this.urlPath = urlPath;
    this.onOpen = options.onOpen || (() => {});
    this.onMessage = options.onMessage || (() => {});
    this.onClose = options.onClose || (() => {});
    this.onReconnecting = options.onReconnecting || (() => {});
    
    this.ws = null;
    this.reconnectDelay = options.reconnectDelay || 2000;
    this.maxReconnectDelay = options.maxReconnectDelay || 10000;
    this.currentDelay = this.reconnectDelay;
    this.shouldReconnect = true;
  }

  connect() {
    this.shouldReconnect = false;
    this.onClose();
  }

  handleClose() {
    this.onClose();
  }

  send(data) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      const msg = typeof data === 'string' ? data : JSON.stringify(data);
      this.ws.send(msg);
      return true;
    }
    return false;
  }

  close() {
    this.shouldReconnect = false;
    if (this.ws) {
      this.ws.close();
    }
  }
}

// The loopback capability token — locks /api/exec + the command WebSockets against other apps on the
// device. Provided IN-PROCESS by the WebView host (AndroidBridge.getCap() on Android; window.__ssCap on
// the Windows head). Never on the wire, so a foreign app can't read it; random per boot, so it can't guess.
export function capToken() {
  return (window.__ssCap || "");
}

export async function executeCommand(command) {
  const res = await fetch("/api/exec", {
    method: "POST",
    headers: { "X-Subsystem-Cap": capToken() },
    body: command,
  });
  if (!res.ok) throw new Error(`HTTP error ${res.status}`);
  const json = await res.json();
  if (json && json.error) throw new Error(json.error);
  return json;
}
