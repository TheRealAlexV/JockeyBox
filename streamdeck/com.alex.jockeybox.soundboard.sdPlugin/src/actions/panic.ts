import { action, type KeyDownEvent, SingletonAction, type WillAppearEvent } from "@elgato/streamdeck";

import { log, warn } from "../log.js";
import { bridge } from "../runtime.js";

/**
 * `com.alex.jockeybox.soundboard.panic` — fires `POST /stop-all`.
 *
 * The soundboard panel has its own stop button; this exists so a panicking
 * operator can reach it without leaving the deck. It is deliberately a second
 * action type rather than a mode of the pad action.
 */
@action({ UUID: "com.alex.jockeybox.soundboard.panic" })
export class PanicAction extends SingletonAction {
  override onWillAppear(ev: WillAppearEvent): void {
    if (!ev.action.isKey()) return;
    // The manifest already supplies the artwork; the title is the only thing the
    // plugin owns here, and `UserTitleEnabled: false` keeps it from being edited.
    void ev.action.setTitle("STOP ALL").catch((error: unknown) => warn("panic title failed", error));
    log("panic key shown");
  }

  override async onKeyDown(ev: KeyDownEvent): Promise<void> {
    try {
      await bridge.stopAll();
      await ev.action.showOk().catch(() => undefined);
    } catch (error) {
      warn("stop-all failed", error);
      await ev.action.showAlert().catch(() => undefined);
    }
  }
}
