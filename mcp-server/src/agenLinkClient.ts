import net from "node:net";
import { randomUUID } from "node:crypto";

export interface AgenLinkResponse {
  id: string;
  ok: boolean;
  data?: unknown;
  error?: string;
}

/**
 * Send one command to the in-editor Agen-Link and await one response.
 *
 * Connect-per-request: the Unity listener restarts on every domain reload (script recompile), so opening a
 * fresh socket each call is the most resilient approach — there is no stale connection to recover.
 */
export function agenLinkRequest(
  port: number,
  command: string,
  params: Record<string, unknown> = {},
  timeoutMs = 15000,
): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const id = randomUUID();
    const socket = net.createConnection({ host: "127.0.0.1", port });
    let buffer = "";
    let settled = false;

    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.removeAllListeners();
      socket.destroy();
      fn();
    };

    const timer = setTimeout(() => {
      finish(() =>
        reject(
          new Error(
            `Agen-Link timed out after ${timeoutMs}ms on 127.0.0.1:${port}. The Unity Editor most likely ` +
              `parked its main thread because its window is unfocused or has been idle — a backgrounded ` +
              `Editor stops processing until it regains focus. Click the Unity Editor window once to wake ` +
              `it, then retry. (If you just edited scripts it may instead be mid-recompile — retry shortly.)`,
          ),
        ),
      );
    }, timeoutMs);

    socket.on("connect", () => {
      socket.write(JSON.stringify({ id, command, params }) + "\n");
    });

    socket.on("data", (chunk: Buffer) => {
      buffer += chunk.toString("utf8");
      const nl = buffer.indexOf("\n");
      if (nl < 0) return; // wait for the full line
      const line = buffer.slice(0, nl);
      let resp: AgenLinkResponse;
      try {
        resp = JSON.parse(line) as AgenLinkResponse;
      } catch (e) {
        finish(() => reject(new Error(`Agen-Link sent malformed JSON: ${(e as Error).message}`)));
        return;
      }
      if (resp.ok) finish(() => resolve(resp.data));
      else finish(() => reject(new Error(resp.error ?? "Agen-Link returned an error")));
    });

    socket.on("error", (err: Error) => {
      finish(() =>
        reject(
          new Error(
            `Cannot reach the Agen-Link on 127.0.0.1:${port} (${err.message}). ` +
              `Open your Unity project and the Agen-Link window, then try again.`,
          ),
        ),
      );
    });

    socket.on("close", () => {
      finish(() =>
        reject(
          new Error(
            "Agen-Link closed the connection before responding — this usually means the Editor is doing a " +
              "domain reload (a recompile after a script change). Wait a moment and retry.",
          ),
        ),
      );
    });
  });
}
