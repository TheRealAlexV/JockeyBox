import type { Bridge } from "./bridge.js";
import { Bridge as BridgeImpl } from "./bridge.js";

/**
 * The one {@link Bridge} instance.
 *
 * It lives in its own module so the actions and the plugin entry point share it
 * without an import cycle (the actions need the bridge, the entry point needs the
 * actions).
 */
export const bridge: Bridge = new BridgeImpl();
