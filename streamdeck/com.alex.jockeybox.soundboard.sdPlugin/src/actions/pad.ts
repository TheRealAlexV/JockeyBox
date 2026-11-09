import {
  action,
  type DidReceiveSettingsEvent,
  type KeyAction,
  type KeyDownEvent,
  SingletonAction,
  type WillAppearEvent,
  type WillDisappearEvent,
} from "@elgato/streamdeck";

import type { KeyView, Snapshot } from "../bridge.js";
import { warn } from "../log.js";
import { keyImage, shortTitle } from "../render.js";
import { bridge } from "../runtime.js";
import type { PadSettings } from "../types.js";

/** One key on the deck, bound to zero or one JockeyBox pad via `settings.soundId`. */
class PadKey implements KeyView {
  private settings: PadSettings = {};
  private lastImage = "";
  private lastTitle: string | undefined;

  constructor(
    readonly id: string,
    private readonly key: KeyAction<PadSettings>,
  ) {}

  updateSettings(settings: PadSettings | undefined): void {
    this.settings = settings ?? {};
  }

  get soundId(): string {
    return (this.settings.soundId ?? "").trim();
  }

  /** Decide image and title from the snapshot, and only send what changed. */
  async paint(snapshot: Snapshot): Promise<void> {
    const soundId = this.soundId;
    const sound = soundId ? snapshot.sounds.get(soundId) : undefined;

    let image: string;
    let title: string;
    if (!soundId) {
      image = keyImage("empty");
      title = "PICK PAD";
    } else if (snapshot.status === "offline" || snapshot.status === "unauthorized") {
      // The pad may be fine — the app is not reachable, which the key says plainly.
      image = keyImage("error");
      title = shortTitle(sound?.name ?? "APP DOWN");
    } else if (snapshot.playing.has(soundId)) {
      image = keyImage("playing");
      title = shortTitle(sound?.name ?? soundId);
    } else if (!sound) {
      image = keyImage("empty");
      title = "NOT FOUND";
    } else {
      image = keyImage("idle");
      title = shortTitle(sound.name);
    }

    if (image !== this.lastImage) {
      this.lastImage = image;
      await this.key
        .setImage(image)
        .catch((error: unknown) => warn(`setImage failed for ${this.id}`, error));
    }
    if (title !== this.lastTitle) {
      this.lastTitle = title;
      await this.key
        .setTitle(title)
        .catch((error: unknown) => warn(`setTitle failed for ${this.id}`, error));
    }
  }

  /** Press: fire the pad, or explain why nothing can happen. */
  async press(): Promise<void> {
    const soundId = this.soundId;
    if (!soundId) {
      await this.key.showAlert().catch(() => undefined);
      return;
    }
    try {
      await bridge.play(soundId);
      await this.key.showOk().catch(() => undefined);
    } catch {
      // The bridge already recorded why; the key just needs to flash at the user.
      await this.key.showAlert().catch(() => undefined);
    }
  }
}

/**
 * `com.alex.jockeybox.soundboard.pad` — a soundboard pad.
 *
 * The manifest sets `UserTitleEnabled: false` and `DisableAutomaticStates: true`
 * because the plugin draws both the title and the artwork itself.
 */
@action({ UUID: "com.alex.jockeybox.soundboard.pad" })
export class PadAction extends SingletonAction<PadSettings> {
  private keys = new Map<string, PadKey>();

  override async onWillAppear(ev: WillAppearEvent<PadSettings>): Promise<void> {
    if (!ev.action.isKey()) return;
    const key = new PadKey(ev.action.id, ev.action);
    key.updateSettings(ev.payload.settings);
    this.keys.set(ev.action.id, key);
    bridge.registerKey(key);
  }

  override onWillDisappear(ev: WillDisappearEvent<PadSettings>): void {
    this.keys.delete(ev.action.id);
    bridge.unregisterKey(ev.action.id);
  }

  override onDidReceiveSettings(ev: DidReceiveSettingsEvent<PadSettings>): void {
    const key = this.keys.get(ev.action.id);
    if (!key) return;
    key.updateSettings(ev.payload.settings);
    bridge.invalidate();
  }

  override async onKeyDown(ev: KeyDownEvent<PadSettings>): Promise<void> {
    const key = this.keys.get(ev.action.id);
    if (!key) return;
    await key.press();
  }
}
