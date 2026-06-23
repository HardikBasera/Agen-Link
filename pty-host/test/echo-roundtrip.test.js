const assert = require('node:assert');
const test = require('node:test');
const net = require('node:net');
const { spawn } = require('node:child_process');
const path = require('node:path');
const { encode, Decoder, T } = require('../protocol');

function startHost(env) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [path.join(__dirname, '..', 'index.js')],
      { env: { ...process.env, ...env } });
    child.stdout.on('data', (d) => {
      const m = String(d).match(/UBPTY_PORT=(\d+)/);
      if (m) resolve({ child, port: Number(m[1]) });
    });
  });
}

test('client authenticates, drives the child, receives echoed output', async () => {
  const childScript = 'process.stdin.setRawMode(true);process.stdin.on("data",d=>process.stdout.write("ECHO:"+d));';
  const { child, port } = await startHost({
    UBPTY_TOKEN: 'secret',
    UBPTY_CMD: process.execPath,
    UBPTY_ARGS: JSON.stringify(['-e', childScript]),
  });

  const got = await new Promise((resolve, reject) => {
    const sock = net.connect(port, '127.0.0.1');
    const dec = new Decoder();
    let acc = '';
    sock.on('connect', () => {
      sock.write(encode(T.AUTH, Buffer.from('secret')));
      setTimeout(() => sock.write(encode(T.INPUT, Buffer.from('ping\n'))), 100);
    });
    sock.on('data', (b) => {
      for (const f of dec.push(b)) {
        if (f.type === T.OUTPUT) { acc += f.payload.toString(); if (acc.includes('ECHO:ping')) resolve(acc); }
      }
    });
    sock.on('error', reject);
    setTimeout(() => reject(new Error('timeout, got: ' + JSON.stringify(acc))), 4000);
  });

  assert.ok(got.includes('ECHO:ping'));
  child.kill();
});

test('client with wrong token is rejected', async () => {
  const { child, port } = await startHost({
    UBPTY_TOKEN: 'right', UBPTY_CMD: process.execPath, UBPTY_ARGS: JSON.stringify(['-e', '0']),
  });
  const closed = await new Promise((resolve) => {
    const sock = net.connect(port, '127.0.0.1');
    sock.on('connect', () => sock.write(encode(T.AUTH, Buffer.from('wrong'))));
    sock.on('close', () => resolve(true));
    setTimeout(() => resolve(false), 2000);
  });
  assert.strictEqual(closed, true);
  child.kill();
});
