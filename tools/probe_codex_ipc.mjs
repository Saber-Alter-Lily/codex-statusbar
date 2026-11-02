// Local IPC diagnostic. Metadata counts only: never records payloads, IDs or paths.
// Usage: node tools/probe_codex_ipc.mjs [--seconds 20] [--log NEW_FILE]
// No default log; --log creates a new file exclusively (never appends/overwrites).
import net from "node:net";
import fs from "node:fs";
import { randomUUID } from "node:crypto";

const argv = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = argv.indexOf(name);
  return i >= 0 ? argv[i + 1] : fallback;
};
const seconds = Number(arg("--seconds", "20"));
if (!Number.isFinite(seconds) || seconds < 1 || seconds > 300) {
  throw new Error("--seconds must be between 1 and 300");
}
const logPath = arg("--log", null);
if (argv.includes("--log") && (!logPath || logPath.startsWith("--"))) {
  throw new Error("--log requires a new output filename");
}
const logFd = logPath ? fs.openSync(logPath, "wx", 0o600) : null;
const counts = { frames: 0, invalidJson: 0, oversized: 0, errors: 0,
  request: 0, response: 0, broadcast: 0, other: 0, routeChanges: 0 };
const sock = net.connect("\\\\.\\pipe\\codex-ipc");
const start = Date.now();
let buffer = Buffer.alloc(0);
let finished = false;
let timer;

function finish() {
  if (finished) return;
  finished = true;
  clearTimeout(timer);
  sock.destroy();
  const report = JSON.stringify({ listenedMs: Date.now() - start, counts });
  console.log(report);
  if (logFd !== null) {
    try { fs.writeSync(logFd, report + "\n"); }
    finally { fs.closeSync(logFd); }
  }
  process.exitCode = counts.errors || counts.oversized ? 1 : 0;
}

sock.on("connect", () => {
  const body = Buffer.from(JSON.stringify({ type: "request", requestId: randomUUID(),
    sourceClientId: "codex-statusbar-probe", version: 0, method: "initialize",
    params: { clientType: "codex-statusbar-probe" } }));
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length);
  sock.write(Buffer.concat([header, body]));
});
sock.on("error", () => { counts.errors++; finish(); });
sock.on("close", finish);
sock.on("data", chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  while (buffer.length >= 4) {
    const length = buffer.readUInt32LE(0);
    if (length > 8 * 1024 * 1024) { counts.oversized++; finish(); return; }
    if (buffer.length < 4 + length) return;
    const body = buffer.subarray(4, 4 + length);
    buffer = buffer.subarray(4 + length);
    counts.frames++;
    try {
      const message = JSON.parse(body.toString("utf8"));
      const type = message?.type;
      if (type === "request" || type === "response" || type === "broadcast") counts[type]++;
      else counts.other++;
      if (message?.method === "thread-stream-following-changed") counts.routeChanges++;
    } catch { counts.invalidJson++; }
  }
});
timer = setTimeout(finish, seconds * 1000);
