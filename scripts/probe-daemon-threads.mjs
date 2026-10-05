// Read-only probe of the shared app-server daemon: `codex app-server proxy` bridges stdio to the
// daemon's control socket, which speaks WebSocket (text frames = JSON-RPC messages).
// Lists loaded threads, their goals and recent threads. Never starts turns or changes goals.
import { spawn } from "node:child_process";
import crypto from "node:crypto";

const proc = spawn("codex app-server proxy", { shell: true, stdio: ["pipe", "pipe", "inherit"] });
const pending = new Map();
let nextId = 1;
let buf = Buffer.alloc(0);
let upgraded = false;
let onOpen;
const opened = new Promise((r) => (onOpen = r));

function sendText(text) {
  const payload = Buffer.from(text);
  const mask = crypto.randomBytes(4);
  let header;
  if (payload.length < 126) header = Buffer.from([0x81, 0x80 | payload.length]);
  else if (payload.length < 65536) { header = Buffer.alloc(4); header[0] = 0x81; header[1] = 0x80 | 126; header.writeUInt16BE(payload.length, 2); }
  else { header = Buffer.alloc(10); header[0] = 0x81; header[1] = 0x80 | 127; header.writeBigUInt64BE(BigInt(payload.length), 2); }
  const masked = Buffer.from(payload.map((b, i) => b ^ mask[i % 4]));
  proc.stdin.write(Buffer.concat([header, mask, masked]));
}

function onMessage(text) {
  const msg = JSON.parse(text);
  if (msg.id !== undefined && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  else if (msg.method) console.log(`<<< ${msg.method} ` + JSON.stringify(msg.params ?? {}).slice(0, 300));
}

proc.stdout.on("data", (chunk) => {
  buf = Buffer.concat([buf, chunk]);
  if (!upgraded) {
    const end = buf.indexOf("\r\n\r\n");
    if (end < 0) return;
    console.log("handshake:", buf.subarray(0, buf.indexOf("\r\n")).toString());
    buf = buf.subarray(end + 4); upgraded = true; onOpen();
  }
  for (;;) {
    if (buf.length < 2) return;
    let len = buf[1] & 0x7f, off = 2;
    if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
    if (buf.length < off + len) return;
    const opcode = buf[0] & 0x0f, data = buf.subarray(off, off + len);
    buf = buf.subarray(off + len);
    if (opcode === 1) onMessage(data.toString());
  }
});

const call = (method, params) => new Promise((resolve) => { const id = nextId++; pending.set(id, resolve); sendText(JSON.stringify({ jsonrpc: "2.0", id, method, params })); });

const key = crypto.randomBytes(16).toString("base64");
proc.stdin.write(`GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
await opened;

const init = await call("initialize", { clientInfo: { name: "fermata-probe", title: null, version: "0.0.1" }, capabilities: { experimentalApi: false, requestAttestation: false } });
console.log("initialize:", JSON.stringify(init.result ?? init.error).slice(0, 300));
sendText(JSON.stringify({ jsonrpc: "2.0", method: "initialized", params: {} }));

const loaded = await call("thread/loaded/list", {});
console.log("thread/loaded/list:", JSON.stringify(loaded.result ?? loaded.error));
for (const id of loaded.result?.data ?? []) {
  const goal = await call("thread/goal/get", { threadId: id });
  console.log(`goal ${id}:`, JSON.stringify(goal.result ?? goal.error).slice(0, 400));
}
const list = await call("thread/list", { limit: 3, useStateDbOnly: true });
for (const t of list.result?.data ?? []) console.log("thread:", JSON.stringify({ id: t.id, status: t.status, cwd: t.cwd, source: t.source, updatedAt: t.updatedAt }));
if (list.error) console.log("thread/list error:", JSON.stringify(list.error));
setTimeout(() => { proc.kill(); process.exit(0); }, 1500);
