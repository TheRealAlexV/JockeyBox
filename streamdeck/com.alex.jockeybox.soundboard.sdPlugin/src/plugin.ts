import streamDeck from "@elgato/streamdeck";

import { PanicAction } from "./actions/panic.js";
import { PadAction } from "./actions/pad.js";
import { normalizeSettings } from "./config.js";
import { log, logError } from "./log.js";
import { bridge } from "./runtime.js";

/**
 * JockeyBox soundboard pads for Stream Deck.
 *
 * Wiring, in order:
 *  1. register the action types,
 *  2. route the property inspector's datasource requests,
 *  3. read the global settings (port + token) and connect the bridge,
 *  4. repaint everything after the machine wakes up.
 */

streamDeck.actions.registerAction(new PadAction());
streamDeck.actions.registerAction(new PanicAction());

// The inspector runs on a `file://` origin and cannot call JockeyBox directly
// (that would need CORS), so it asks the plugin instead. The plugin is the only
// side that touches the app's loopback API.
streamDeck.ui.onSendToPlugin(async (ev) => {
  const request = (ev.payload ?? {}) as { event?: unknown };
  switch (request.event) {
    case "hello": {
      await streamDeck.ui.sendToPropertyInspector({
        event: "settings",
        port: bridge.endpoint.port,
        token: bridge.endpoint.token,
      });
      return;
    }
    case "getSounds": {
      const result = await bridge.listSoundsForInspector();
      await streamDeck.ui.sendToPropertyInspector({
        event: "sounds",
        ok: result.ok,
        items: result.items,
        message: result.message,
        port: result.port,
        tokenSet: result.tokenSet,
      });
      return;
    }
    case "testConnection": {
      const result = await bridge.testConnection();
      await streamDeck.ui.sendToPropertyInspector({
        event: "connection",
        ok: result.ok,
        message: result.message,
        port: result.port,
      });
      return;
    }
    default:
      log("ignored property inspector message", request);
  }
});

streamDeck.settings.onDidReceiveGlobalSettings((ev) => {
  bridge.setConfig(normalizeSettings(ev.settings));
});

streamDeck.system.onSystemDidWakeUp(() => {
  void bridge.handleWake();
});

async function main(): Promise<void> {
  await streamDeck.connect();

  const settings = await streamDeck.settings.getGlobalSettings();
  bridge.setConfig(normalizeSettings(settings));
  bridge.start();

  log(
    `ready: plugin ${streamDeck.info.plugin?.version ?? "?"} on Stream Deck ${streamDeck.info.application.version}, ` +
      `API at 127.0.0.1:${bridge.endpoint.port}`,
  );
}

main().catch((error: unknown) => {
  logError("startup failed", error);
  // Rethrowing would leave Stream Deck with a dead plugin and no clue why.
  process.exitCode = 1;
});
