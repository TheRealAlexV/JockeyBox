import type { GlobalSettings } from "./types.js";

/** `LocalApiServer.DefaultPort`; the app steps upward from here if the port is busy. */
export const DEFAULT_PORT = 17890;

export type EndpointConfig = {
  port: number;
  token: string;
};

export const DEFAULT_CONFIG: EndpointConfig = { port: DEFAULT_PORT, token: "" };

/**
 * Accept whatever the property inspector (or a hand-edited settings file) wrote
 * and produce a usable endpoint. Bad input falls back to the defaults instead of
 * throwing, because this runs on every settings event.
 */
export function normalizeSettings(raw: unknown): EndpointConfig {
  const source = (raw ?? {}) as GlobalSettings;

  let port = DEFAULT_PORT;
  const rawPort = source.port;
  if (typeof rawPort === "number" && Number.isFinite(rawPort)) {
    port = Math.trunc(rawPort);
  } else if (typeof rawPort === "string" && rawPort.trim() !== "") {
    const parsed = Number.parseInt(rawPort.trim(), 10);
    if (Number.isFinite(parsed)) port = parsed;
  }
  if (!Number.isInteger(port) || port < 1 || port > 65535) port = DEFAULT_PORT;

  const token = typeof source.token === "string" ? source.token.trim() : "";

  return { port, token };
}

export function sameConfig(a: EndpointConfig, b: EndpointConfig): boolean {
  return a.port === b.port && a.token === b.token;
}
