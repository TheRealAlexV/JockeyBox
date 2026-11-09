/**
 * Shapes of the data that crosses the plugin boundary.
 *
 * The API types mirror `JockeyBox.Api.ApiPad` / `ApiState` in
 * `src/JockeyBox/Api/LocalApiServer.cs`. The server serialises with
 * `JsonNamingPolicy.CamelCase`, so the JSON keys are lower-camel.
 */

/** Per-key settings, stored by Stream Deck. */
export type PadSettings = {
  /** Id of the JockeyBox pad this key fires (`ApiPad.Id`). */
  soundId?: string;
};

/**
 * Plugin-wide settings, edited from the property inspector.
 *
 * The port is configurable because JockeyBox walks upward from 17890 when that
 * port is taken, and the token is the `ApiToken` from JockeyBox settings.
 */
export type GlobalSettings = {
  port?: number | string;
  token?: string;
};

/** `GET /sounds` item. */
export type ApiPad = {
  id: string;
  name: string;
  hotkey?: string | null;
  volume?: number;
  loop?: boolean;
  isPlaying?: boolean;
  durationSeconds?: number;
};

/** `GET /state`. */
export type ApiState = {
  playing?: string[];
  master?: number;
  anyDeckPlaying?: boolean;
  transition?: string;
};

/** `{type:"state", state:{…}}` frame from `WS /ws`. */
export type StateFrame = {
  type?: string;
  state?: ApiState;
};
