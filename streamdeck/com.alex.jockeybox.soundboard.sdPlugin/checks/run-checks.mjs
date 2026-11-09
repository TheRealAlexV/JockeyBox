/**
 * Offline assertions for the parts of the plugin that do not need Stream Deck:
 * generated SVG art, the HTTP client's URLs, settings normalisation, and the
 * bridge's optimistic/server-truth state machine.
 *
 * Run with `npm run checks`. Exit code is non-zero on the first failure.
 */
import assert from "node:assert/strict";

import { ApiError, JockeyBoxClient } from "./.build/api.mjs";
import { Bridge } from "./.build/bridge.mjs";
import { normalizeSettings } from "./.build/config.mjs";
import { keyImage, shortTitle } from "./.build/render.mjs";

const ok = (label) => console.log(`  ok  ${label}`);

// ------------------------------------------------------------------ render
{
  const uri = keyImage("playing");
  assert.ok(uri.startsWith("data:image/svg+xml;charset=utf-8,"), "svg data uri prefix");
  const svg = decodeURIComponent(uri.slice(uri.indexOf(",") + 1));
  assert.ok(svg.startsWith("<svg"), "decodes to svg");
  assert.ok(svg.includes('width="72"') && svg.includes('height="72"'), "72x72");
  assert.equal(keyImage("playing"), uri, "cached");
  assert.notEqual(keyImage("playing"), keyImage("idle"), "states differ");
  for (const mode of ["playing", "idle", "empty", "error"]) {
    const decoded = decodeURIComponent(keyImage(mode).split(",")[1]);
    assert.ok(decoded.endsWith("</svg>"), `${mode} is well-formed`);
  }
  assert.equal(shortTitle("Snare"), "Snare");
  assert.equal(shortTitle("x".repeat(40)).length, 24, "truncates to max");
  ok("render: 4 SVG key states, 72x72, cached, title truncation");
}

// ----------------------------------------------------------------- config
{
  assert.deepEqual(normalizeSettings({}), { port: 17890, token: "" });
  assert.deepEqual(normalizeSettings({ port: "17895", token: "  abc  " }), { port: 17895, token: "abc" });
  assert.equal(normalizeSettings({ port: 99999 }).port, 17890, "out-of-range port falls back");
  assert.equal(normalizeSettings({ port: "nope" }).port, 17890, "garbage port falls back");
  assert.equal(normalizeSettings({ port: 17891.7 }).port, 17891, "port is an integer");
  ok("config: defaults, string/number ports, clamping, token trim");
}

// -------------------------------------------------------------------- api
{
  const calls = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify({ ok: true, id: "abc" }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };

  const client = new JockeyBoxClient({ port: 17890, token: "tok" });
  await client.play("11111111-2222-3333-4444-555555555555");
  await client.stopAll();
  assert.equal(
    calls[0].url,
    "http://127.0.0.1:17890/sounds/11111111-2222-3333-4444-555555555555/play",
  );
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.headers["X-JockeyBox-Token"], "tok");
  assert.equal(calls[1].url, "http://127.0.0.1:17890/stop-all");
  assert.equal(client.webSocketUrl, "ws://127.0.0.1:17890/ws?token=tok");

  const noToken = new JockeyBoxClient({ port: 19000, token: "" });
  assert.equal(noToken.webSocketUrl, "ws://127.0.0.1:19000/ws");
  assert.equal(noToken.baseUrl, "http://127.0.0.1:19000");

  // 401 maps to the "auth" kind so the key can say "check the token".
  globalThis.fetch = async () => new Response("unauthorized", { status: 401 });
  await assert.rejects(
    () => new JockeyBoxClient({ port: 17890, token: "bad" }).getSounds(),
    (error) => error instanceof ApiError && error.kind === "auth",
  );

  // A refused connection is an offline error, not a crash.
  globalThis.fetch = async () => {
    throw new TypeError("fetch failed");
  };
  await assert.rejects(
    () => new JockeyBoxClient({ port: 17890, token: "" }).getState(),
    (error) => error instanceof ApiError && error.kind === "offline",
  );

  globalThis.fetch = originalFetch;
  ok("api: exact paths/methods, token header, 401 → auth, refused → offline");
}

// ----------------------------------------------------------------- bridge
{
  const sounds = [
    { id: "a", name: "Airhorn", hotkey: "F1" },
    { id: "b", name: "Rimshot" },
  ];
  let state = { playing: ["b"], anyDeckPlaying: true };
  let failNext = false;
  const seen = [];
  globalThis.fetch = async (url) => {
    const path = new URL(String(url)).pathname;
    seen.push(path);
    if (failNext) throw new TypeError("connect ECONNREFUSED");
    if (path === "/sounds") return new Response(JSON.stringify(sounds));
    if (path === "/state") return new Response(JSON.stringify(state));
    if (path.endsWith("/play")) {
      state = { ...state, playing: [...state.playing, "a"] };
      return new Response(JSON.stringify({ ok: true }));
    }
    if (path === "/stop-all") {
      state = { playing: [], anyDeckPlaying: false };
      return new Response(JSON.stringify({ ok: true }));
    }
    return new Response("not found", { status: 404 });
  };

  const bridge = new Bridge();
  bridge.setConfig({ port: 17890, token: "" });
  const key = {
    id: "k1",
    paints: 0,
    async paint() {
      key.paints += 1;
    },
  };
  bridge.registerKey(key);

  await bridge.refresh("test");
  let snap = bridge.snapshot();
  assert.equal(snap.status, "online");
  assert.equal(snap.sounds.size, 2);
  assert.ok(snap.playing.has("b"), "server state lights the playing pad");
  assert.ok(!snap.playing.has("a"));

  // Optimistic: pressing shows immediately, before the server confirms.
  await bridge.play("a");
  snap = bridge.snapshot();
  assert.ok(snap.playing.has("a"), "optimistic playing");
  assert.ok(snap.playing.has("b"), "server playing preserved");

  // Server truth arrives over WS and wins.
  bridge.handleStateFrame({ type: "state", state: { playing: ["b"] } });
  snap = bridge.snapshot();
  assert.ok(!snap.playing.has("a"), "optimistic entry reverted by state frame");

  // A failed play must not leave the key lit.
  failNext = true;
  await assert.rejects(() => bridge.play("a"));
  snap = bridge.snapshot();
  assert.ok(!snap.playing.has("a"), "failed play reverts");
  assert.equal(snap.status, "offline");
  assert.ok(snap.message.includes("127.0.0.1:17890"), "message names the endpoint");
  failNext = false;

  await bridge.stopAll();
  snap = bridge.snapshot();
  assert.equal(snap.playing.size, 0, "stop-all clears the lights");
  assert.equal(snap.anyDeckPlaying, false);

  const listed = await bridge.listSoundsForInspector();
  assert.equal(listed.ok, true);
  assert.deepEqual(
    listed.items.map((item) => item.label),
    ["Airhorn  ·  F1", "Rimshot"],
  );
  assert.ok(seen.includes("/sounds") && seen.includes("/state"));
  ok("bridge: refresh, optimistic press, WS revert, failure revert, stop-all, inspector list");
}

console.log("all offline checks passed");
