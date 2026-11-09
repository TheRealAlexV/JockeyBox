import { ApiError, JockeyBoxClient } from "./api.js";
import { DEFAULT_CONFIG, sameConfig, type EndpointConfig } from "./config.js";
import { log, logError, warn } from "./log.js";
import type { ApiPad, ApiState, StateFrame } from "./types.js";

/** How often keys may be repainted at most. `setImage`/`setTitle` are expensive. */
const PAINT_INTERVAL_MS = 125;
/** Safety net: a slow re-read in case a socket frame is missed. */
const SAFETY_REFRESH_MS = 20000;
/** How long a key keeps its optimistic "playing" look without server confirmation. */
const OPTIMISTIC_MS = 2000;
/** A state frame younger than this at press time may predate the play request. */
const OPTIMISTIC_ARM_MS = 400;
const WS_MIN_BACKOFF_MS = 500;
const WS_MAX_BACKOFF_MS = 15000;

export type ConnectionStatus = "unknown" | "online" | "unauthorized" | "offline";

export type Snapshot = {
  status: ConnectionStatus;
  /** Pads by id, as last successfully read from `GET /sounds`. */
  sounds: Map<string, ApiPad>;
  /** Ids that should look active: server state plus unconfirmed local presses. */
  playing: Set<string>;
  anyDeckPlaying: boolean;
  /** Human-readable reason for `status !== "online"`. */
  message: string;
  updatedAt: number;
};

/** A visible key, as the bridge sees it. */
export interface KeyView {
  readonly id: string;
  paint(snapshot: Snapshot): Promise<void>;
}

type Optimistic = {
  /** Wall-clock deadline for the optimistic highlight. */
  until: number;
  /** When the press happened; used to ignore in-flight state frames. */
  armedAt: number;
};

/**
 * Single source of truth for what every key should show.
 *
 * Keys do not talk to JockeyBox themselves. They read the latest {@link Snapshot}
 * handed to `paint`, and pressing one routes through here so state changes fan
 * out to every key at once (a pad started from the app lights up on the deck too).
 */
export class Bridge {
  private config: EndpointConfig = { ...DEFAULT_CONFIG };
  private client = new JockeyBoxClient(this.config);

  private sounds = new Map<string, ApiPad>();
  private serverPlaying = new Set<string>();
  private optimistic = new Map<string, Optimistic>();
  private anyDeckPlaying = false;
  private status: ConnectionStatus = "unknown";
  private message = "not connected yet";
  private updatedAt = 0;

  private keys = new Map<string, KeyView>();

  private paintTimer: ReturnType<typeof setTimeout> | undefined;
  private optimisticTimer: ReturnType<typeof setTimeout> | undefined;
  private safetyTimer: ReturnType<typeof setInterval> | undefined;
  private refreshInFlight: Promise<void> | undefined;

  private socket: WebSocket | undefined;
  private socketBackoff = WS_MIN_BACKOFF_MS;
  private socketTimer: ReturnType<typeof setTimeout> | undefined;
  private stopped = true;

  // ------------------------------------------------------------- lifecycle

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;

    this.safetyTimer = setInterval(() => {
      void this.refresh("safety");
    }, SAFETY_REFRESH_MS);
    this.safetyTimer.unref?.();

    this.connectSocket();
    void this.refresh("start");
  }

  stop(): void {
    this.stopped = true;
    if (this.safetyTimer) clearInterval(this.safetyTimer);
    if (this.paintTimer) clearTimeout(this.paintTimer);
    if (this.optimisticTimer) clearTimeout(this.optimisticTimer);
    if (this.socketTimer) clearTimeout(this.socketTimer);
    this.safetyTimer = undefined;
    this.paintTimer = undefined;
    this.optimisticTimer = undefined;
    this.socketTimer = undefined;
    this.closeSocket();
  }

  /** Called whenever the global settings (port / token) change. */
  setConfig(next: EndpointConfig): void {
    if (sameConfig(this.config, next)) return;
    this.config = next;
    this.client = new JockeyBoxClient(next);
    log(`endpoint is now ${this.client.baseUrl}${next.token ? " (token set)" : " (no token)"}`);
    if (this.stopped) return;

    // A different port means a different socket; drop it and start over.
    this.closeSocket();
    this.socketBackoff = WS_MIN_BACKOFF_MS;
    this.status = "unknown";
    this.invalidate();
    this.connectSocket();
    void this.refresh("config");
  }

  get endpoint(): EndpointConfig {
    return this.config;
  }

  // ------------------------------------------------------------- keys

  registerKey(key: KeyView): void {
    this.keys.set(key.id, key);
    // Paint immediately so a freshly added key never shows the manifest default
    // while it waits for the next fan-out.
    void key.paint(this.snapshot()).catch((error: unknown) => warn(`paint failed for ${key.id}`, error));
    if (this.status === "unknown") void this.refresh("appear");
  }

  unregisterKey(id: string): void {
    this.keys.delete(id);
  }

  /** Hand a snapshot to every visible key, but at most every {@link PAINT_INTERVAL_MS}. */
  invalidate(): void {
    if (this.paintTimer) return;
    this.paintTimer = setTimeout(() => {
      this.paintTimer = undefined;
      void this.paintAll();
    }, PAINT_INTERVAL_MS);
    this.paintTimer.unref?.();
  }

  private async paintAll(): Promise<void> {
    const snapshot = this.snapshot();
    const results = await Promise.allSettled([...this.keys.values()].map((key) => key.paint(snapshot)));
    for (const result of results) {
      if (result.status === "rejected") warn("key paint failed", result.reason);
    }
  }

  snapshot(): Snapshot {
    const playing = new Set(this.serverPlaying);
    const now = Date.now();
    for (const [id, entry] of this.optimistic) {
      if (entry.until > now) playing.add(id);
    }
    return {
      status: this.status,
      sounds: new Map(this.sounds),
      playing,
      anyDeckPlaying: this.anyDeckPlaying || playing.size > 0,
      message: this.message,
      updatedAt: this.updatedAt,
    };
  }

  // ------------------------------------------------------------- commands

  /** Press a pad: optimistic highlight first, then the request. */
  async play(id: string): Promise<void> {
    this.markPlaying(id);
    try {
      await this.client.play(id);
    } catch (error) {
      this.optimistic.delete(id);
      this.recordFailure(error, `play ${id}`);
      this.invalidate();
      throw error;
    }
  }

  async stopAll(): Promise<void> {
    try {
      await this.client.stopAll();
      this.optimistic.clear();
      // Reflect the stop at once; the socket will confirm with a state frame.
      this.serverPlaying = new Set();
      this.anyDeckPlaying = false;
      this.invalidate();
    } catch (error) {
      this.recordFailure(error, "stop-all");
      throw error;
    }
  }

  /** Re-read `/sounds` for the property inspector's dropdown. */
  async listSoundsForInspector(): Promise<{
    ok: boolean;
    items: { value: string; label: string }[];
    message: string;
    port: number;
    tokenSet: boolean;
  }> {
    const tokenSet = this.config.token !== "";
    try {
      const sounds = await this.client.getSounds();
      this.sounds = new Map(sounds.map((pad) => [pad.id, pad]));
      this.status = "online";
      this.message = `${sounds.length} pads`;
      this.updatedAt = Date.now();
      this.invalidate();
      return {
        ok: true,
        items: toItems(sounds),
        message: `${sounds.length} pad${sounds.length === 1 ? "" : "s"} found on port ${this.config.port}`,
        port: this.config.port,
        tokenSet,
      };
    } catch (error) {
      const apiError = error instanceof ApiError ? error : undefined;
      this.status = apiError?.kind === "auth" ? "unauthorized" : "offline";
      this.message = apiError?.message ?? String(error);
      this.invalidate();
      return {
        ok: false,
        items: toItems([...this.sounds.values()]),
        message: this.message,
        port: this.config.port,
        tokenSet,
      };
    }
  }

  /** Probe used by the property inspector's "Test" button. */
  async testConnection(): Promise<{ ok: boolean; message: string; port: number }> {
    try {
      const state = await this.client.getState();
      this.applyState(state, "http");
      this.status = "online";
      this.message = "connected";
      this.updatedAt = Date.now();
      this.invalidate();
      const playing = state.playing?.length ?? 0;
      return {
        ok: true,
        message: `connected to 127.0.0.1:${this.config.port} · ${playing} pad${playing === 1 ? "" : "s"} playing`,
        port: this.config.port,
      };
    } catch (error) {
      const apiError = error instanceof ApiError ? error : undefined;
      this.status = apiError?.kind === "auth" ? "unauthorized" : "offline";
      this.message = apiError?.message ?? String(error);
      this.invalidate();
      return { ok: false, message: this.message, port: this.config.port };
    }
  }

  /** After sleep, sockets are usually dead even though they still look open. */
  async handleWake(): Promise<void> {
    log("system woke up; re-reading state and reconnecting");
    this.closeSocket();
    this.socketBackoff = WS_MIN_BACKOFF_MS;
    this.connectSocket();
    await this.refresh("wake");
  }

  // ------------------------------------------------------------- state

  /** Re-read both endpoints; de-duplicated so presses and timers cannot pile up. */
  refresh(reason: string): Promise<void> {
    if (this.refreshInFlight) return this.refreshInFlight;
    this.refreshInFlight = this.doRefresh(reason).finally(() => {
      this.refreshInFlight = undefined;
    });
    return this.refreshInFlight;
  }

  private async doRefresh(reason: string): Promise<void> {
    try {
      const [sounds, state] = await Promise.all([this.client.getSounds(), this.client.getState()]);
      this.sounds = new Map(sounds.map((pad) => [pad.id, pad]));
      this.applyState(state, "http");
      if (this.status !== "online") log(`connected to JockeyBox at ${this.client.baseUrl} (${reason})`);
      this.status = "online";
      this.message = `${this.sounds.size} pads`;
      this.updatedAt = Date.now();
    } catch (error) {
      this.recordFailure(error, `refresh/${reason}`);
    }
    this.invalidate();
  }

  private applyState(state: ApiState, source: "http" | "ws"): void {
    const playing = Array.isArray(state.playing) ? state.playing.filter((id) => typeof id === "string") : [];
    this.serverPlaying = new Set(playing);
    this.anyDeckPlaying = typeof state.anyDeckPlaying === "boolean" ? state.anyDeckPlaying : playing.length > 0;
    this.updatedAt = Date.now();

    // Server truth supersedes optimistic guesses, except for frames that were
    // already in flight when the key was pressed.
    const now = Date.now();
    for (const [id, entry] of this.optimistic) {
      if (source === "http" && now - entry.armedAt < OPTIMISTIC_ARM_MS) continue;
      this.optimistic.delete(id);
    }
  }

  private markPlaying(id: string): void {
    this.optimistic.set(id, { until: Date.now() + OPTIMISTIC_MS, armedAt: Date.now() });
    this.scheduleOptimisticExpiry();
    this.invalidate();
  }

  private scheduleOptimisticExpiry(): void {
    if (this.optimisticTimer || this.optimistic.size === 0) return;
    let soonest = Number.POSITIVE_INFINITY;
    for (const entry of this.optimistic.values()) soonest = Math.min(soonest, entry.until);
    const delay = Math.max(0, soonest - Date.now()) + 10;
    this.optimisticTimer = setTimeout(() => {
      this.optimisticTimer = undefined;
      const now = Date.now();
      let changed = false;
      for (const [id, entry] of this.optimistic) {
        if (entry.until <= now) {
          this.optimistic.delete(id);
          changed = true;
        }
      }
      if (this.optimistic.size > 0) this.scheduleOptimisticExpiry();
      if (changed) this.invalidate();
    }, delay);
    this.optimisticTimer.unref?.();
  }

  private recordFailure(error: unknown, context: string): void {
    const apiError = error instanceof ApiError ? error : undefined;
    switch (apiError?.kind) {
      case "auth":
        this.status = "unauthorized";
        this.message = "token rejected — check the token in the property inspector";
        break;
      case "offline":
        this.status = "offline";
        this.message = apiError.message;
        break;
      case "http":
        this.status = "offline";
        this.message = apiError.message;
        break;
      default:
        this.status = "offline";
        this.message = error instanceof Error ? error.message : String(error);
    }
    // Offline is the normal state when the app is closed; keep the noise low.
    if (apiError?.kind === "auth" || apiError === undefined) {
      logError(`${context} failed:`, error);
    } else {
      warn(`${context} failed:`, apiError.message);
    }
  }

  // ------------------------------------------------------------- socket

  private connectSocket(): void {
    if (this.stopped || this.socket) return;
    if (typeof WebSocket !== "function") {
      warn("this Node runtime has no global WebSocket; falling back to polling");
      return;
    }

    const url = this.client.webSocketUrl;
    let socket: WebSocket;
    try {
      socket = new WebSocket(url);
    } catch (error) {
      warn("could not open state socket", error);
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;

    socket.addEventListener("open", () => {
      this.socketBackoff = WS_MIN_BACKOFF_MS;
      log(`state socket open (${url.replace(/\?.*$/, "")})`);
      void this.refresh("socket-open");
    });

    socket.addEventListener("message", (event: MessageEvent) => {
      const data = (event as MessageEvent).data;
      if (typeof data === "string") {
        this.handleSocketMessage(data);
        return;
      }
      // Node hands over Buffers for binary frames; the app only sends text.
      const maybeText = data as { text?: () => Promise<string> } | undefined;
      if (typeof maybeText?.text === "function") {
        maybeText
          .text()
          .then((text) => this.handleSocketMessage(text))
          .catch(() => undefined);
      }
    });

    socket.addEventListener("error", () => {
      // `close` always follows; nothing to do beyond not spamming the log.
    });

    socket.addEventListener("close", () => {
      if (this.socket === socket) this.socket = undefined;
      if (this.stopped) return;
      warn(`state socket closed; retrying in ${this.socketBackoff} ms`);
      this.scheduleReconnect();
      // The socket dying is also the signal that the app may have restarted.
      void this.refresh("socket-closed");
    });
  }

  private handleSocketMessage(raw: string): void {
    let frame: unknown;
    try {
      frame = JSON.parse(raw);
    } catch {
      return;
    }
    this.handleStateFrame(frame);
  }

  /**
   * Apply one `{type:"state", state:{…}}` frame.
   *
   * Public because the socket handler and the offline checks share this path —
   * a second implementation would be a second set of bugs.
   */
  handleStateFrame(frame: unknown): void {
    if (typeof frame !== "object" || frame === null) return;
    const typed = frame as StateFrame;
    if (typed.type !== "state" || !typed.state) return;
    this.applyState(typed.state, "ws");
    if (this.status !== "online") {
      this.status = "online";
      this.message = `${this.sounds.size} pads`;
    }
    this.invalidate();
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.socketTimer) return;
    const delay = this.socketBackoff;
    this.socketBackoff = Math.min(this.socketBackoff * 2, WS_MAX_BACKOFF_MS);
    this.socketTimer = setTimeout(() => {
      this.socketTimer = undefined;
      this.connectSocket();
    }, delay);
    this.socketTimer.unref?.();
  }

  private closeSocket(): void {
    if (this.socketTimer) {
      clearTimeout(this.socketTimer);
      this.socketTimer = undefined;
    }
    const socket = this.socket;
    this.socket = undefined;
    if (!socket) return;
    try {
      socket.close();
    } catch {
      /* already gone */
    }
  }
}

function toItems(sounds: ApiPad[]): { value: string; label: string }[] {
  return [...sounds]
    .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }))
    .map((pad) => ({
      value: pad.id,
      label: pad.hotkey ? `${pad.name}  ·  ${pad.hotkey}` : pad.name,
    }));
}
