/**
 * `npm run e2e` — runs the real `bin/plugin.js` against a fake Stream Deck host and
 * a fake JockeyBox API, talking a minimal RFC6455 WebSocket in both directions.
 *
 * This is the closest thing to a live deck that can run unattended: it proves the
 * bundle registers, paints, plays, reverts, panics and answers the inspector. Only
 * `node` built-ins are used, so it needs no extra dependencies.
 */
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import http from "node:http";
import { fileURLToPath } from "node:url";

const PLUGIN_DIR = fileURLToPath(new URL("..", import.meta.url));
const UUID = "com.alex.jockeybox.soundboard";
const TOKEN = "tok-123";

// ------------------------------------------------------------ tiny ws server

function acceptKey(key) {
  return crypto
    .createHash("sha1")
    .update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`)
    .digest("base64");
}

function encodeFrame(text, opcode = 1) {
  const payload = Buffer.from(text, "utf8");
  const len = payload.length;
  let header;
  if (len < 126) {
    header = Buffer.from([0x80 | opcode, len]);
  } else if (len < 65536) {
    header = Buffer.alloc(4);
    header[0] = 0x80 | opcode;
    header[1] = 126;
    header.writeUInt16BE(len, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x80 | opcode;
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(len), 2);
  }
  return Buffer.concat([header, payload]);
}

/** Minimal server-side socket wrapper: text messages in, text messages out. */
function wrapSocket(socket, onMessage) {
  let buffer = Buffer.alloc(0);
  const api = {
    send(obj) {
      socket.write(encodeFrame(typeof obj === "string" ? obj : JSON.stringify(obj)));
    },
    close() {
      try {
        socket.write(encodeFrame("", 8));
        socket.end();
      } catch {}
    },
  };

  socket.on("data", (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 2) {
      const opcode = buffer[0] & 0x0f;
      const masked = (buffer[1] & 0x80) !== 0;
      let len = buffer[1] & 0x7f;
      let offset = 2;
      if (len === 126) {
        if (buffer.length < 4) return;
        len = buffer.readUInt16BE(2);
        offset = 4;
      } else if (len === 127) {
        if (buffer.length < 10) return;
        len = Number(buffer.readBigUInt64BE(2));
        offset = 10;
      }
      const maskLength = masked ? 4 : 0;
      if (buffer.length < offset + maskLength + len) return;
      const mask = masked ? buffer.subarray(offset, offset + 4) : null;
      const data = Buffer.from(buffer.subarray(offset + maskLength, offset + maskLength + len));
      buffer = buffer.subarray(offset + maskLength + len);
      if (mask) for (let i = 0; i < data.length; i += 1) data[i] ^= mask[i % 4];

      if (opcode === 8) {
        socket.end();
        return;
      }
      if (opcode === 9) {
        socket.write(encodeFrame(data.toString(), 10));
        continue;
      }
      if (opcode === 1) onMessage(data.toString());
    }
  });
  socket.on("error", () => undefined);
  return api;
}

function createWsServer(onConnection) {
  const server = http.createServer((req, res) => {
    res.writeHead(426);
    res.end();
  });
  server.on("upgrade", (req, socket) => {
    socket.write(
      "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        `Sec-WebSocket-Accept: ${acceptKey(req.headers["sec-websocket-key"])}\r\n\r\n`,
    );
    onConnection(wrapSocket(socket, (text) => {
      let msg;
      try {
        msg = JSON.parse(text);
      } catch {
        return;
      }
      current?.onMessage(msg);
    }), req);
  });
  const current = { onMessage: () => undefined };
  return { server, current };
}

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function waitFor(predicate, label, timeout = 5000) {
  const started = Date.now();
  while (Date.now() - started < timeout) {
    if (predicate()) return;
    await wait(25);
  }
  throw new Error(`timed out waiting for: ${label}`);
}

// -------------------------------------------------------------- fake JockeyBox

const apiState = { playing: [], anyDeckPlaying: false };
const apiRequests = [];
const apiSocketRef = { send: () => undefined };

const api = http.createServer((req, res) => {
  const url = new URL(req.url, "http://127.0.0.1");
  apiRequests.push(`${req.method} ${url.pathname} token=${req.headers["x-jockeybox-token"] ?? ""}`);
  const json = (body) => {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };
  if (req.method === "GET" && url.pathname === "/sounds") {
    return json([
      { id: "a", name: "Airhorn", hotkey: "F1", isPlaying: false },
      { id: "b", name: "Rimshot", hotkey: null, isPlaying: apiState.playing.includes("b") },
    ]);
  }
  if (req.method === "GET" && url.pathname === "/state") return json(apiState);
  if (req.method === "POST" && /^\/sounds\/[^/]+\/play$/.test(url.pathname)) {
    const id = url.pathname.split("/")[2];
    if (id !== "a" && id !== "b") {
      res.writeHead(404);
      return res.end("{}");
    }
    apiState.playing = [...apiState.playing, id];
    apiState.anyDeckPlaying = true;
    return json({ ok: true, id });
  }
  if (req.method === "POST" && url.pathname === "/stop-all") {
    apiState.playing = [];
    apiState.anyDeckPlaying = false;
    return json({ ok: true });
  }
  res.writeHead(404);
  res.end("{}");
});
const apiSockets = new Set();
api.on("connection", (socket) => apiSockets.add(socket));
api.on("upgrade", (req, socket) => {
  apiSockets.add(socket);
  const key = req.headers["sec-websocket-key"];
  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
      `Sec-WebSocket-Accept: ${acceptKey(key)}\r\n\r\n`,
  );
  apiSocketRef.send = (obj) => socket.write(encodeFrame(JSON.stringify(obj)));
  socket.on("error", () => undefined);
});

await new Promise((resolve) => api.listen(0, "127.0.0.1", resolve));
const apiPort = api.address().port;

// ------------------------------------------------------------- fake SD host

const fromPlugin = [];
const hostRef = { send: () => undefined };
const host = http.createServer((req, res) => {
  res.writeHead(426);
  res.end();
});
host.on("upgrade", (req, socket) => {
  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
      `Sec-WebSocket-Accept: ${acceptKey(req.headers["sec-websocket-key"])}\r\n\r\n`,
  );
  const ws = wrapSocket(socket, (text) => {
    let msg;
    try {
      msg = JSON.parse(text);
    } catch {
      return;
    }
    fromPlugin.push(msg);
    if (msg.event === "registerPlugin") {
      // Answer the plugin's getGlobalSettings with the fake API's port.
      hostRef.send({
        event: "didReceiveGlobalSettings",
        payload: { settings: { port: apiPort, token: TOKEN } },
      });
    }
  });
  hostRef.send = (obj) => ws.send(obj);
  hostRef.close = () => ws.close();
});
await new Promise((resolve) => host.listen(0, "127.0.0.1", resolve));
const hostPort = host.address().port;

// ---------------------------------------------------------------- run plugin

const child = spawn(
  process.execPath,
  [
    "bin/plugin.js",
    "-port", String(hostPort),
    "-pluginUUID", UUID,
    "-registerEvent", "registerPlugin",
    "-info", JSON.stringify({
      application: { font: "1", language: "en", platform: "windows", platformVersion: "10.0.22631", version: "7.6.0" },
      devices: [{ id: "dev1", name: "Stream Deck", size: { columns: 5, rows: 3 }, type: 0 }],
      plugin: { uuid: UUID, version: "1.0.0.0" },
    }),
  ],
  { cwd: PLUGIN_DIR, stdio: ["ignore", "pipe", "pipe"] },
);

let stderr = "";
child.stderr.on("data", (chunk) => {
  stderr += chunk.toString();
  process.stderr.write(`    | ${chunk.toString()}`);
});
child.stdout.on("data", (chunk) => process.stderr.write(`    o ${chunk.toString()}`));

const actions = []; // {event, context, payload}
function send(event, context, payload) {
  const actionId = context === "key-panic" ? `${UUID}.panic` : `${UUID}.pad`;
  hostRef.send({ event, action: actionId, context, device: "dev1", payload });
}
function sentByPlugin(event) {
  return fromPlugin.filter((m) => m.event === event);
}

try {
  await waitFor(() => sentByPlugin("registerPlugin").length === 1, "plugin registration");
  const registration = sentByPlugin("registerPlugin")[0];
  assert.equal(registration.uuid, UUID, "registered under the manifest UUID");

  // A pad key becomes visible, bound to "a".
  send("willAppear", "key-a", {
    settings: { soundId: "a" },
    coordinates: { column: 0, row: 0 },
    controller: "Keypad",
    isInMultiAction: false,
  });
  await waitFor(
    () => sentByPlugin("setTitle").some((m) => m.payload.title === "Airhorn" && m.context === "key-a"),
    "pad key title",
  );
  await waitFor(
    () =>
      sentByPlugin("setImage").some(
        (m) => m.context === "key-a" && decodeURIComponent(m.payload.image).includes('fill="#8A94A6"'),
      ),
    "idle art",
  );
  console.log("  ok  willAppear → /sounds + /state, key painted grey with pad name");

  // Pressing it plays the pad.
  send("keyDown", "key-a", { settings: { soundId: "a" }, isInMultiAction: false });
  await waitFor(() => apiRequests.some((r) => r === `POST /sounds/a/play token=${TOKEN}`), "play POST");
  await waitFor(
    () => sentByPlugin("setImage").some((m) => m.context === "key-a" && decodeURIComponent(m.payload.image).includes('fill="#5AB0FF"')),
    "optimistic playing art",
  );
  console.log("  ok  keyDown → POST /sounds/a/play with the token header, key turns blue immediately");

  // Server truth moves the highlight to "b" (started from the app).
  const keyB = { id: "key-b", settings: { soundId: "b" } };
  send("willAppear", "key-b", {
    settings: { soundId: "b" },
    coordinates: { column: 1, row: 0 },
    controller: "Keypad",
    isInMultiAction: false,
  });
  await waitFor(() => sentByPlugin("setTitle").some((m) => m.context === "key-b"), "second key painted");

  apiState.playing = ["b"];
  apiSocketRef.send({ type: "state", state: { playing: ["b"], anyDeckPlaying: true } });
  await waitFor(
    () => sentByPlugin("setImage").some((m) => m.context === "key-b" && decodeURIComponent(m.payload.image).includes('fill="#5AB0FF"')),
    "WS frame lights key b",
  );
  await waitFor(
    () =>
      sentByPlugin("setImage")
        .filter((m) => m.context === "key-a")
        .at(-1) &&
      decodeURIComponent(sentByPlugin("setImage").filter((m) => m.context === "key-a").at(-1).payload.image).includes('fill="#8A94A6"'),
    "WS frame dims key a",
  );
  console.log("  ok  WS state frame → the playing pad lights up, the optimistic key reverts");

  // Panic key.
  send("willAppear", "key-panic", {
    settings: {},
    coordinates: { column: 2, row: 0 },
    controller: "Keypad",
    isInMultiAction: false,
  });
  await waitFor(() => sentByPlugin("setTitle").some((m) => m.context === "key-panic" && m.payload.title === "STOP ALL"), "panic title");
  send("keyDown", "key-panic", { settings: {}, isInMultiAction: false });
  await waitFor(() => apiRequests.some((r) => r === `POST /stop-all token=${TOKEN}`), "stop-all POST");
  console.log("  ok  panic key → POST /stop-all");

  // Property inspector datasource round-trip. Stream Deck first announces the PI
  // for a given action instance, then forwards its messages with that context.
  hostRef.send({ event: "propertyInspectorDidAppear", action: `${UUID}.pad`, context: "key-a", device: "dev1", payload: {} });
  hostRef.send({
    event: "sendToPlugin",
    action: `${UUID}.pad`,
    context: "key-a",
    device: "dev1",
    payload: { event: "getSounds" },
  });
  await waitFor(() => sentByPlugin("sendToPropertyInspector").length > 0, "inspector reply");
  const reply = sentByPlugin("sendToPropertyInspector")[0].payload;
  assert.equal(reply.event, "sounds");
  assert.equal(reply.ok, true);
  assert.deepEqual(reply.items.map((i) => i.label), ["Airhorn  ·  F1", "Rimshot"]);
  console.log("  ok  inspector datasource round-trip returns the pad list");

  // Wake-up: the plugin re-reads both endpoints and repaints.
  const before = apiRequests.filter((r) => r === `GET /state token=${TOKEN}`).length;
  hostRef.send({ event: "systemDidWakeUp", context: UUID, payload: {} });
  await waitFor(
    () => apiRequests.filter((r) => r === `GET /state token=${TOKEN}`).length > before,
    "state refresh after wake",
  );
  console.log("  ok  systemDidWakeUp → state re-read (keys repaint from the fresh snapshot)");

  // Unbound key and unknown pad are distinguishable.
  send("willAppear", "key-empty", {
    settings: {},
    coordinates: { column: 3, row: 0 },
    controller: "Keypad",
    isInMultiAction: false,
  });
  await waitFor(() => sentByPlugin("setTitle").some((m) => m.context === "key-empty" && m.payload.title === "PICK PAD"), "empty key");
  send("willAppear", "key-gone", {
    settings: { soundId: "deleted-pad" },
    coordinates: { column: 4, row: 0 },
    controller: "Keypad",
    isInMultiAction: false,
  });
  await waitFor(() => sentByPlugin("setTitle").some((m) => m.context === "key-gone" && m.payload.title === "NOT FOUND"), "missing pad");
  console.log("  ok  unbound → PICK PAD, deleted pad → NOT FOUND");

  // Unknown pad id pressed → alert, no crash.
  send("keyDown", "key-gone", { settings: { soundId: "deleted-pad" }, isInMultiAction: false });
  await waitFor(() => sentByPlugin("showAlert").some((m) => m.context === "key-gone"), "alert on unknown pad");
  console.log("  ok  pressing a deleted pad shows the alert");

  // App goes away: the keys say so instead of going stale.
  for (const socket of apiSockets) socket.destroy();
  await new Promise((resolve) => api.close(() => resolve()));
  hostRef.send({ event: "systemDidWakeUp", context: UUID, payload: {} });
  await waitFor(
    () =>
      sentByPlugin("setImage").some(
        (m) => m.context === "key-a" && decodeURIComponent(m.payload.image).includes('stroke="#FF6B5E"'),
      ),
    "error art when the app is down",
  );
  await waitFor(
    () => sentByPlugin("setTitle").some((m) => m.context === "key-a" && m.payload.title === "Airhorn"),
    "title preserved while offline",
  );
  console.log("  ok  app down → red ! on the keys, titles preserved");

  console.log("end-to-end harness passed");
} catch (error) {
  console.error("FAILED:", error.message);
  console.error("stderr from plugin:\n" + stderr);
  console.error("messages from plugin:\n" + JSON.stringify(fromPlugin, null, 1).slice(0, 4000));
  process.exitCode = 1;
} finally {
  child.kill("SIGTERM");
  hostRef.close?.();
  apiSocketRef.send = () => undefined;
  host.close();
  api.close();
  setTimeout(() => process.exit(process.exitCode ?? 0), 200);
}
