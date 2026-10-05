// Fake `claude` CLI for end-to-end testing Fermata's Claude Code jobs without a real session.
// Implements what Fermata uses, with the behaviour observed on Claude Code 2.1.289:
//   claude --bg [--permission-mode M] [--name N] "prompt"   -> "backgrounded · <id>"
//   claude --bg --resume <session-id> "prompt"              -> continues the same id (flags would copy)
//   claude agents --json --all                               -> JSON array
//   claude stop <id> | claude mcp add|remove …
// State lives in FAKE_CLAUDE_STATE (JSON). Every call is appended to FAKE_CLAUDE_LOG.
// Tests drive the session with FAKE_CLAUDE_STATE edits: { sessions: { <id>: { alive, status, state } } }.
import fs from "node:fs";
import crypto from "node:crypto";

const statePath = process.env.FAKE_CLAUDE_STATE ?? "fake-claude-state.json";
const logPath = process.env.FAKE_CLAUDE_LOG;
const args = process.argv.slice(2);
const load = () => (fs.existsSync(statePath) ? JSON.parse(fs.readFileSync(statePath, "utf8")) : { sessions: {} });
const save = (s) => fs.writeFileSync(statePath, JSON.stringify(s, null, 2));
if (logPath) fs.appendFileSync(logPath, JSON.stringify({ cwd: process.cwd(), args }) + "\n");

const state = load();
const flag = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };

if (args[0] === "agents") {
  const list = Object.values(state.sessions).map((s) => ({
    ...(s.alive ? { pid: 4242 } : {}),
    id: s.id, cwd: s.cwd, kind: "background", startedAt: s.startedAt, sessionId: s.sessionId, name: s.name,
    ...(s.alive ? { status: s.status ?? "busy" } : {}), state: s.state ?? "running",
  }));
  console.log(JSON.stringify(list, null, 2));
} else if (args[0] === "stop") {
  const s = state.sessions[args[1]];
  if (s) { s.alive = false; s.state = "stopped"; save(state); }
  console.log(`stopped ${args[1]}`);
} else if (args[0] === "mcp") {
  console.log("ok");
} else if (args.includes("--bg")) {
  if (process.env.FAKE_CLAUDE_UNTRUSTED) { console.log(`Workspace not trusted. Run \`claude\` in ${process.cwd()} once and accept the trust prompt, then retry.`); process.exit(0); }
  const resume = flag("--resume");
  let s;
  if (resume) {
    s = Object.values(state.sessions).find((x) => x.sessionId === resume);
    if (!s) { console.error(`No conversation found with session ID: ${resume}`); process.exit(1); }
    s.alive = true; s.status = "busy"; s.state = "running"; s.lastPrompt = args[args.length - 1];
  } else {
    const sessionId = crypto.randomUUID();
    s = { id: sessionId.slice(0, 8), sessionId, cwd: process.cwd(), startedAt: Date.now(), name: flag("--name") ?? "", alive: true, status: "busy", state: "running", permissionMode: flag("--permission-mode"), lastPrompt: args[args.length - 1] };
    state.sessions[s.id] = s;
  }
  save(state);
  console.log("Starting background service…");
  console.log(`backgrounded · ${s.id}`);
} else {
  console.error(`fake-claude: unsupported arguments ${args.join(" ")}`);
  process.exit(2);
}
