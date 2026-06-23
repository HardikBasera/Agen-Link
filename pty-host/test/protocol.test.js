const assert = require('node:assert');
const test = require('node:test');
const { encode, Decoder, T } = require('../protocol');

test('encode/decode round-trips a single frame', () => {
  const buf = encode(T.OUTPUT, Buffer.from('hello'));
  const dec = new Decoder();
  const frames = dec.push(buf);
  assert.strictEqual(frames.length, 1);
  assert.strictEqual(frames[0].type, T.OUTPUT);
  assert.strictEqual(frames[0].payload.toString(), 'hello');
});

test('Decoder reassembles a frame split across chunks', () => {
  const buf = encode(T.INPUT, Buffer.from('abcd'));
  const dec = new Decoder();
  assert.strictEqual(dec.push(buf.subarray(0, 3)).length, 0);
  const frames = dec.push(buf.subarray(3));
  assert.strictEqual(frames.length, 1);
  assert.strictEqual(frames[0].payload.toString(), 'abcd');
});

test('Decoder returns multiple frames from one chunk', () => {
  const buf = Buffer.concat([encode(T.PING, Buffer.alloc(0)), encode(T.PONG, Buffer.alloc(0))]);
  const frames = new Decoder().push(buf);
  assert.strictEqual(frames.length, 2);
});

test('Decoder handles zero-length payloads', () => {
  const buf = encode(T.PING, Buffer.alloc(0));
  const frames = new Decoder().push(buf);
  assert.strictEqual(frames.length, 1);
  assert.strictEqual(frames[0].type, T.PING);
  assert.strictEqual(frames[0].payload.length, 0);
});

test('Decoder extracts many frames from one chunk', () => {
  const parts = [];
  for (let i = 0; i < 12; i++) parts.push(encode(T.OUTPUT, Buffer.from('f' + i)));
  const frames = new Decoder().push(Buffer.concat(parts));
  assert.strictEqual(frames.length, 12);
  assert.strictEqual(frames[11].payload.toString(), 'f11');
});

test("Decoder passes through unrecognized types (validation is the handler's job)", () => {
  const frames = new Decoder().push(encode(0xFF, Buffer.from('x')));
  assert.strictEqual(frames[0].type, 0xFF);
});
