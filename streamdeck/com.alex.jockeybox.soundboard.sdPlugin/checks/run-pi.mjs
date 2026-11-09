/**
 * `npm run pi` — headless smoke test for `ui/pi.js` with a fake DOM and a fake
 * Stream Deck socket. Verifies the handshake, the dropdown and the settings the
 * inspector writes; it does not test layout (that needs the real app).
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const source = readFileSync(new URL("../ui/pi.js", import.meta.url), "utf8");

class Element {
  constructor(id) {
    this.id = id;
    this.children = [];
    this.listeners = new Map();
    this.value = "";
    this.textContent = "";
    this.dataset = {};
    this.hidden = false;
    this._html = "";
  }
  set innerHTML(v) {
    this._html = v;
    this.children = [];
  }
  get innerHTML() {
    return this._html;
  }
  appendChild(child) {
    this.children.push(child);
    return child;
  }
  addEventListener(type, fn) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }
  dispatch(type, event = {}) {
    for (const fn of this.listeners.get(type) ?? []) fn({ preventDefault() {}, ...event });
  }
  blur() {}
}

const ids = ["sound", "soundField", "port", "token", "status", "hint", "refresh", "test", "title", "foot"];
const elements = new Map(ids.map((id) => [id, new Element(id)]));

const document = {
  activeElement: null,
  getElementById: (id) => elements.get(id) ?? null,
  createElement: () => new Element("option"),
};

const sent = [];
let handlers = new Map();
class FakeSocket {
  static OPEN = 1;
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    handlers = new Map();
    sockets.push(this);
  }
  addEventListener(type, fn) {
    handlers.set(type, [...(handlers.get(type) ?? []), fn]);
  }
  send(data) {
    sent.push(JSON.parse(data));
  }
  emit(type, event = {}) {
    for (const fn of handlers.get(type) ?? []) fn(event);
  }
  close() {}
}
const sockets = [];

const sandbox = {
  document,
  window: { setTimeout, connectElgatoStreamDeckSocket: undefined },
  WebSocket: FakeSocket,
  console,
  setTimeout,
  JSON,
  Number,
  Array,
  String,
  Boolean,
};
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
vm.runInContext(source, sandbox);

// Stream Deck calls this on inspector load.
sandbox.window.connectElgatoStreamDeckSocket(
  "9223",
  "inspector-ctx",
  "registerPropertyInspector",
  "{}",
  JSON.stringify({ action: "com.alex.jockeybox.soundboard.pad", context: "key-ctx", payload: { settings: {} } }),
);

const socket = sockets[0];
assert.ok(socket, "opened a socket to the Stream Deck port");
assert.equal(socket.url, "ws://127.0.0.1:9223");

socket.emit("open");
const registered = sent.find((m) => m.event === "registerPropertyInspector");
assert.ok(registered, "registered the property inspector");
assert.equal(registered.uuid, "inspector-ctx");
assert.ok(
  sent.some((m) => m.event === "getGlobalSettings" && m.context === "com.alex.jockeybox.soundboard.pad"),
  "asked for global settings with the plugin uuid as context",
);
const hello = sent.find((m) => m.event === "sendToPlugin");
assert.deepEqual(hello, {
  event: "sendToPlugin",
  action: "key-ctx",
  context: "inspector-ctx",
  payload: { event: "hello" },
});

// Global settings arrive: fields fill in.
socket.emit("message", {
  data: JSON.stringify({ event: "didReceiveGlobalSettings", payload: { settings: { port: 17895, token: "sekret" } } }),
});
assert.equal(elements.get("port").value, "17895");
assert.equal(elements.get("token").value, "sekret");

// Plugin answers the datasource request.
socket.emit("message", {
  data: JSON.stringify({
    event: "sendToPlugin",
    payload: {
      event: "sounds",
      ok: true,
      items: [
        { value: "id-a", label: "Airhorn  ·  F1" },
        { value: "id-b", label: "Rimshot" },
      ],
      message: "2 pads found on port 17895",
      port: 17895,
      tokenSet: true,
    },
  }),
});
const select = elements.get("sound");
assert.equal(select.children.length, 3, "placeholder + 2 pads");
assert.deepEqual(
  select.children.slice(1).map((o) => o.value),
  ["id-a", "id-b"],
);
assert.equal(elements.get("status").textContent, "2 pads found on port 17895");
assert.equal(elements.get("status").dataset.kind, "good");

// Choosing a pad saves the binding to this key's settings only.
select.value = "id-b";
select.dispatch("change");
const binding = sent.find((m) => m.event === "setSettings");
assert.ok(binding, "saved the binding");
assert.equal(binding.context, "key-ctx");
assert.deepEqual(binding.payload, { soundId: "id-b" });

// Changing the port saves the plugin-wide settings under the plugin uuid.
const port = elements.get("port");
port.value = "18000";
port.dispatch("change");
const saved = sent.filter((m) => m.event === "setGlobalSettings").at(-1);
assert.ok(saved, "saved global settings");
assert.equal(saved.context, "com.alex.jockeybox.soundboard.pad");
assert.deepEqual(saved.payload, { port: 18000, token: "sekret" });

// A refused pad is reported, not swallowed.
socket.emit("message", {
  data: JSON.stringify({
    event: "sendToPlugin",
    payload: { event: "sounds", ok: false, items: [], message: "cannot reach JockeyBox on 127.0.0.1:18000", port: 18000 },
  }),
});
assert.equal(elements.get("status").dataset.kind, "bad");
assert.match(elements.get("status").textContent, /127\.0\.0\.1:18000/);

// Panic keys reuse the inspector but hide the pad picker.
const panicSandbox = { ...sandbox };
vm.createContext(panicSandbox);
vm.runInContext(source.replace(/^\s*$/m, ""), panicSandbox);
vm.runInContext(
  `window.connectElgatoStreamDeckSocket("9223","insp2","registerPropertyInspector","{}", ${JSON.stringify(
    JSON.stringify({ action: "com.alex.jockeybox.soundboard.panic", context: "panic-ctx", payload: { settings: {} } }),
  )});`,
  panicSandbox,
);
sockets.at(-1).emit("open");
assert.equal(elements.get("soundField").hidden, true, "panic key hides the pad picker");
assert.equal(elements.get("title").textContent, "JockeyBox panic");

console.log("pi.js smoke test passed");
