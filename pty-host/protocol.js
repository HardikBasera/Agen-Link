const T = { AUTH: 0x00, INPUT: 0x01, RESIZE: 0x02, PING: 0x03,
            HELLO: 0x80, OUTPUT: 0x81, EXIT: 0x82, PONG: 0x83 };

function encode(type, payload) {
  payload = payload || Buffer.alloc(0);
  const buf = Buffer.alloc(5 + payload.length);
  buf.writeUInt8(type, 0);
  buf.writeUInt32BE(payload.length, 1);
  if (payload.length > 0) payload.copy(buf, 5);
  return buf;
}

class Decoder {
  constructor() { this.buf = Buffer.alloc(0); }
  push(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    const frames = [];
    while (this.buf.length >= 5) {
      const len = this.buf.readUInt32BE(1);
      if (this.buf.length < 5 + len) break;
      frames.push({ type: this.buf.readUInt8(0), payload: Buffer.from(this.buf.subarray(5, 5 + len)) });
      this.buf = this.buf.subarray(5 + len);
    }
    return frames;
  }
}

module.exports = { T, encode, Decoder };
