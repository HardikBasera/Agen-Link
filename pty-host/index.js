const net = require('node:net');
const pty = require('node-pty');
const { encode, Decoder, T } = require('./protocol');

const TOKEN = process.env.UBPTY_TOKEN || '';
const CMD = process.env.UBPTY_CMD;
const ARGS = JSON.parse(process.env.UBPTY_ARGS || '[]');
const CWD = process.env.UBPTY_CWD || process.cwd();
const PARENT_PID = process.env.UBPTY_PARENT_PID ? Number(process.env.UBPTY_PARENT_PID) : 0;
const COLS = Number(process.env.UBPTY_COLS || 100);
const ROWS = Number(process.env.UBPTY_ROWS || 30);
const RING_MAX = 256 * 1024;

let ring = Buffer.alloc(0);
let client = null;
let cols = COLS, rows = ROWS;

const term = pty.spawn(CMD, ARGS, {
  name: 'xterm-256color', cols, rows, cwd: CWD,
  env: { ...process.env, TERM: 'xterm-256color' },
});

function send(type, payload) { if (client) { try { client.write(encode(type, payload)); } catch (_) {} } }

term.onData((d) => {
  const b = Buffer.from(d, 'utf8');
  ring = Buffer.concat([ring, b]);
  if (ring.length > RING_MAX) ring = ring.subarray(ring.length - RING_MAX);
  send(T.OUTPUT, b);
});
term.onExit(({ exitCode }) => {
  const p = Buffer.alloc(4); p.writeInt32BE(exitCode | 0, 0);
  send(T.EXIT, p);
  setTimeout(() => process.exit(0), 50);
});

const server = net.createServer((sock) => {
  if (client) { try { client.destroy(); } catch (_) {} }
  const dec = new Decoder();
  let authed = false;
  sock.on('data', (chunk) => {
    for (const f of dec.push(chunk)) {
      if (!authed) {
        if (f.type === T.AUTH && f.payload.toString() === TOKEN && TOKEN.length > 0) {
          authed = true; client = sock;
          const hello = Buffer.alloc(4); hello.writeUInt16BE(cols, 0); hello.writeUInt16BE(rows, 2);
          sock.write(encode(T.HELLO, hello));
          if (ring.length) sock.write(encode(T.OUTPUT, ring));
        } else { sock.destroy(); return; }
        continue;
      }
      if (f.type === T.INPUT) term.write(f.payload.toString('utf8'));
      else if (f.type === T.RESIZE) {
        cols = f.payload.readUInt16BE(0); rows = f.payload.readUInt16BE(2);
        try { term.resize(cols, rows); } catch (_) {}
      } else if (f.type === T.PING) sock.write(encode(T.PONG, Buffer.alloc(0)));
    }
  });
  sock.on('close', () => { if (client === sock) client = null; });
  sock.on('error', () => {});
});

server.listen(0, '127.0.0.1', () => {
  process.stdout.write(`UBPTY_PORT=${server.address().port}\n`);
});

if (PARENT_PID) {
  setInterval(() => {
    try { process.kill(PARENT_PID, 0); }
    catch (_) { try { term.kill(); } catch (_) {} process.exit(0); }
  }, 3000);
}
