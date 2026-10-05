// Read-only probe of codex app-server: initialize, account/read, account/rateLimits/read.
// Never calls account/rateLimitResetCredit/consume.
import { spawn } from "node:child_process";
import readline from "node:readline";

const t0 = Date.now();
const proc = spawn("codex", ["app-server"], { shell: true, stdio: ["pipe", "pipe", "pipe"] });
const rl = readline.createInterface({ input: proc.stdout });
const pending = new Map();
let nextId = 1;

const redact = (k, v) => (/token|secret|cookie|email|auth/i.test(k) && typeof v === "string" ? "<redacted>" : v);

rl.on("line", (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { console.log("NONJSON", line.slice(0, 200)); return; }
  if (msg.id !== undefined && pending.has(msg.id)) {
    const { method, resolve } = pending.get(msg.id);
    pending.delete(msg.id);
    console.log(`\n<<< ${method} (+${Date.now() - t0}ms)\n` + JSON.stringify(msg.result ?? { error: msg.error }, redact, 2));
    resolve(msg);
  } else {
    console.log(`\n<<< NOTIFICATION/REQUEST ${msg.method} ` + JSON.stringify(msg.params ?? {}, redact).slice(0, 600));
  }
});
proc.stderr.on("data", (d) => process.stderr.write("[stderr] " + d.toString().slice(0, 300)));

function call(method, params) {
  const id = nextId++;
  return new Promise((resolve) => {
    pending.set(id, { method, resolve });
    proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });
}
const notify = (method, params) => proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");

await call("initialize", { clientInfo: { name: "fermata-probe", title: null, version: "0.0.1" }, capabilities: { experimentalApi: false, requestAttestation: false } });
notify("initialized", {});
await call("account/read", { refreshToken: false });
await call("account/rateLimits/read", {});
await call("account/rateLimits/read", { excludeResetCreditDetails: true });
setTimeout(() => { proc.kill(); process.exit(0); }, 3000);
