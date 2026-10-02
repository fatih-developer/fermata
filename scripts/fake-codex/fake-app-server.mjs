// Fake `codex app-server` for end-to-end testing ResetMe without spending real reset credits.
// The account starts rate-limited (5-hour window at 100%) with 2 credits; a consume clears it.
//
// Point ResetMe at it with config.toml:
//   [codex]
//   executable = "<repo>/scripts/fake-codex/codex.cmd"   (Windows)  or  ".../codex" (macOS/Linux)
//
// FAKE_CODEX_STATE: JSON file that keeps state across runs (default: none, in-memory).
// FAKE_CODEX_LOG:   file that receives one line per consume call (key, creditId, outcome).
import fs from "node:fs";
import readline from "node:readline";

const args = process.argv.slice(2);
if (args[0] !== "app-server") {
  process.stderr.write("fake codex: only `app-server` is supported\n");
  process.exit(2);
}

const now = () => Math.floor(Date.now() / 1000);
const statePath = process.env.FAKE_CODEX_STATE;
const logPath = process.env.FAKE_CODEX_LOG;

let state = { resetDone: false, credits: 2, redeemedKeys: [] };
if (statePath && fs.existsSync(statePath)) state = JSON.parse(fs.readFileSync(statePath, "utf8"));
const save = () => statePath && fs.writeFileSync(statePath, JSON.stringify(state));

const window = (used, mins, resetsIn) => ({ usedPercent: used, windowDurationMins: mins, resetsAt: now() + resetsIn });

function rateLimits(params) {
  const blocked = !state.resetDone;
  const snapshot = {
    limitId: "codex",
    primary: blocked ? window(100, 300, 2 * 3600 + 40 * 60) : window(0, 300, 5 * 3600),
    secondary: blocked ? window(64, 10080, 3 * 86400) : window(0, 10080, 7 * 86400),
    rateLimitReachedType: blocked ? "rate_limit_reached" : null,
  };
  const credits = Array.from({ length: state.credits }, (_, i) => ({
    id: `fake-credit-${i + 1}`,
    resetType: "codexRateLimits",
    status: "available",
    grantedAt: now() - 86400,
    expiresAt: now() + (9 + i * 10) * 86400,
    title: "Full reset (Weekly + 5 hr)",
    description: "fake",
  }));
  return {
    ordinaryUsageAllowed: !blocked,
    rateLimits: snapshot,
    rateLimitsByLimitId: { codex: snapshot },
    rateLimitResetCredits: { availableCount: state.credits, credits: params?.excludeResetCreditDetails ? null : credits },
    accountId: "fake-account",
    rateLimitUpsell: null,
  };
}

function consume(params) {
  let outcome;
  if (state.redeemedKeys.includes(params.idempotencyKey)) outcome = "alreadyRedeemed";
  else if (state.resetDone) outcome = "nothingToReset";
  else if (state.credits <= 0) outcome = "noCredit";
  else {
    state.credits -= 1;
    state.resetDone = true;
    state.redeemedKeys.push(params.idempotencyKey);
    outcome = "reset";
  }
  save();
  if (logPath) fs.appendFileSync(logPath, `${params.idempotencyKey} ${params.creditId ?? "-"} ${outcome}\n`);
  return { outcome };
}

const handlers = {
  initialize: () => ({ userAgent: "fake-codex/0.159.3 (fake; x86_64)", codexHome: "/fake/.codex", platformFamily: "fake", platformOs: "fake" }),
  "account/read": () => ({ account: { type: "chatgpt", planType: "plus" }, requiresOpenaiAuth: true }),
  "account/rateLimits/read": rateLimits,
  "account/rateLimitResetCredit/consume": consume,
};

const send = (msg) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...msg }) + "\n");

readline.createInterface({ input: process.stdin }).on("line", (line) => {
  const msg = JSON.parse(line);
  if (msg.id === undefined) return; // notification (initialized)
  const handler = handlers[msg.method];
  if (!handler) return send({ id: msg.id, error: { code: -32601, message: `fake: ${msg.method} not supported` } });
  send({ id: msg.id, result: handler(msg.params) });
});
