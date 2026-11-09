import type { ApiPad, ApiState } from "./types.js";
import type { EndpointConfig } from "./config.js";

export type ApiErrorKind = "offline" | "auth" | "http" | "bad-json";

export class ApiError extends Error {
  constructor(
    message: string,
    readonly kind: ApiErrorKind,
    readonly status?: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

const REQUEST_TIMEOUT_MS = 1500;

/**
 * Minimal loopback client for JockeyBox's local API.
 *
 * The plugin backend is a plain Node process, so there is no CORS layer here —
 * unlike the property inspector, which runs on a `file://` origin and therefore
 * has to talk to this plugin instead of to the app.
 */
export class JockeyBoxClient {
  constructor(private readonly config: EndpointConfig) {}

  get baseUrl(): string {
    return `http://127.0.0.1:${this.config.port}`;
  }

  private headers(): Record<string, string> {
    const headers: Record<string, string> = { Accept: "application/json" };
    // The app also accepts ?token=, but the header works for plain HTTP requests
    // and keeps the token out of the URL.
    if (this.config.token) headers["X-JockeyBox-Token"] = this.config.token;
    return headers;
  }

  private async request(path: string, init: RequestInit = {}): Promise<unknown> {
    let response: Response;
    try {
      response = await fetch(`${this.baseUrl}${path}`, {
        ...init,
        headers: { ...this.headers(), ...(init.headers as Record<string, string> | undefined) },
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      throw new ApiError(
        `cannot reach JockeyBox on 127.0.0.1:${this.config.port} (${detail})`,
        "offline",
      );
    }

    if (response.status === 401) {
      throw new ApiError("JockeyBox rejected the API token", "auth", 401);
    }
    if (!response.ok) {
      throw new ApiError(`JockeyBox answered HTTP ${response.status}`, "http", response.status);
    }

    try {
      return await response.json();
    } catch {
      throw new ApiError("JockeyBox returned a non-JSON body", "bad-json");
    }
  }

  /** `GET /sounds` */
  async getSounds(): Promise<ApiPad[]> {
    const data = await this.request("/sounds");
    if (!Array.isArray(data)) throw new ApiError("GET /sounds did not return an array", "bad-json");
    return data.map(toPad).filter((pad): pad is ApiPad => pad !== null);
  }

  /** `GET /state` */
  async getState(): Promise<ApiState> {
    const data = await this.request("/state");
    if (typeof data !== "object" || data === null) {
      throw new ApiError("GET /state did not return an object", "bad-json");
    }
    return data as ApiState;
  }

  /** `POST /sounds/{id}/play` */
  async play(id: string): Promise<void> {
    // Pad ids are GUIDs, so encoding is a no-op in practice; encode anyway so a
    // stray space or slash can never corrupt the path.
    await this.request(`/sounds/${encodeURIComponent(id)}/play`, { method: "POST" });
  }

  /** `POST /stop-all` */
  async stopAll(): Promise<void> {
    await this.request("/stop-all", { method: "POST" });
  }

  /** `ws://127.0.0.1:<port>/ws` — the app pushes state frames here. */
  get webSocketUrl(): string {
    const query = this.config.token ? `?token=${encodeURIComponent(this.config.token)}` : "";
    return `ws://127.0.0.1:${this.config.port}/ws${query}`;
  }
}

function toPad(value: unknown): ApiPad | null {
  if (typeof value !== "object" || value === null) return null;
  const raw = value as Record<string, unknown>;
  const id = typeof raw.id === "string" ? raw.id : "";
  if (!id) return null;
  return {
    id,
    name: typeof raw.name === "string" && raw.name.trim() !== "" ? raw.name : id,
    hotkey: typeof raw.hotkey === "string" ? raw.hotkey : null,
    volume: typeof raw.volume === "number" ? raw.volume : undefined,
    loop: typeof raw.loop === "boolean" ? raw.loop : undefined,
    isPlaying: typeof raw.isPlaying === "boolean" ? raw.isPlaying : undefined,
    durationSeconds: typeof raw.durationSeconds === "number" ? raw.durationSeconds : undefined,
  };
}
