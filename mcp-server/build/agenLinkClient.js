import net from "node:net";
import { randomUUID } from "node:crypto";
const CONNECT_BUDGET_MS = 10000;
const CONNECT_BACKOFFS_MS = [250, 500, 1000, 2000];
/**
 * Open a socket to the in-editor Agen-Link, retrying transient CONNECT failures.
 *
 * The Unity listener stops and rebinds on every domain reload (script recompile) — a ~1-5s window in which
 * connect fails with ECONNREFUSED. A tool call that lands in that window should ride it out, not error: a
 * flaky-feeling tool trains the CLI to stop calling tools and ask the user / write scripts instead. We only
 * ever retry the CONNECT, never a request that was already written (see agenLinkRequest) — re-sending a
 * mutation like create_gameobject could execute it twice.
 */
function connectWithRetry(port, budgetMs = CONNECT_BUDGET_MS) {
    const start = Date.now();
    let attempt = 0;
    return new Promise((resolve, reject) => {
        const tryOnce = () => {
            const socket = net.createConnection({ host: "127.0.0.1", port });
            let done = false;
            const onConnect = () => {
                if (done)
                    return;
                done = true;
                socket.removeListener("error", onError);
                resolve(socket);
            };
            const onError = (err) => {
                if (done)
                    return;
                done = true;
                socket.removeListener("connect", onConnect);
                socket.destroy();
                const elapsed = Date.now() - start;
                if (elapsed >= budgetMs) {
                    reject(new Error(`Cannot reach the Agen-Link on 127.0.0.1:${port} (${err.message}). Retried for ` +
                        `${Math.round(budgetMs / 1000)}s — open your Unity project and the Agen-Link window, then try again.`));
                    return;
                }
                const backoff = CONNECT_BACKOFFS_MS[Math.min(attempt, CONNECT_BACKOFFS_MS.length - 1)];
                attempt++;
                setTimeout(tryOnce, Math.min(backoff, Math.max(0, budgetMs - elapsed)));
            };
            socket.once("connect", onConnect);
            socket.once("error", onError);
        };
        tryOnce();
    });
}
/**
 * Send one command to the in-editor Agen-Link and await one response.
 *
 * Connect-per-request: the Unity listener restarts on every domain reload, so opening a fresh socket each
 * call is the most resilient approach — there is no stale connection to recover. The response timer starts
 * only AFTER a connection is established, so a slow reconnect does not eat into the command's own timeout.
 */
export function agenLinkRequest(port, command, params = {}, timeoutMs = 15000) {
    const id = randomUUID();
    return connectWithRetry(port).then((socket) => new Promise((resolve, reject) => {
        let buffer = "";
        let settled = false;
        const finish = (fn) => {
            if (settled)
                return;
            settled = true;
            clearTimeout(timer);
            socket.removeAllListeners();
            socket.destroy();
            fn();
        };
        const timer = setTimeout(() => {
            finish(() => reject(new Error(`Agen-Link timed out after ${timeoutMs}ms on 127.0.0.1:${port}. The Unity Editor most likely ` +
                `parked its main thread because its window is unfocused or has been idle — a backgrounded ` +
                `Editor stops processing until it regains focus. Click the Unity Editor window once to wake ` +
                `it, then retry. (If you just edited scripts it may instead be mid-recompile — retry shortly.)`)));
        }, timeoutMs);
        socket.on("data", (chunk) => {
            buffer += chunk.toString("utf8");
            const nl = buffer.indexOf("\n");
            if (nl < 0)
                return; // wait for the full line
            const line = buffer.slice(0, nl);
            let resp;
            try {
                resp = JSON.parse(line);
            }
            catch (e) {
                finish(() => reject(new Error(`Agen-Link sent malformed JSON: ${e.message}`)));
                return;
            }
            if (resp.ok)
                finish(() => resolve(resp.data));
            else
                finish(() => reject(new Error(resp.error ?? "Agen-Link returned an error")));
        });
        socket.on("error", (err) => {
            finish(() => reject(new Error(`Lost the Agen-Link connection on 127.0.0.1:${port} (${err.message}). ` +
                `Open your Unity project and the Agen-Link window, then try again.`)));
        });
        socket.on("close", () => {
            finish(() => reject(new Error("Agen-Link closed the connection before responding — this usually means the Editor is doing a " +
                "domain reload (a recompile after a script change). The bridge restarts within a few seconds; " +
                "call the tool again — do not switch to another approach.")));
        });
        socket.write(JSON.stringify({ id, command, params }) + "\n");
    }));
}
