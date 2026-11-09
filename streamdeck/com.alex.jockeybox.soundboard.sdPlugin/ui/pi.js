/* Property inspector for com.alex.jockeybox.soundboard.pad.
 *
 * Runs on a file:// origin inside Stream Deck, so it never fetches JockeyBox
 * itself. It asks the plugin (over Stream Deck's own socket) for the sound list
 * and for connection tests; the plugin is the only thing that talks to the app.
 */
(() => {
  "use strict";

  const DEFAULT_PORT = 17890;

  /** @type {WebSocket | null} */
  let socket = null;
  let actionUuid = "";
  let inspectorUuid = "";
  let pluginUuid = "";
  let saveTimer = null;
  let ready = false;

  const el = (id) => document.getElementById(id);
  const soundSelect = el("sound");
  const soundField = el("soundField");
  const portInput = el("port");
  const tokenInput = el("token");
  const statusLine = el("status");
  const hint = el("hint");
  const refreshButton = el("refresh");
  const testButton = el("test");

  /** Items from the plugin's last `sounds` reply; kept so settings events can re-render. */
  let currentItems = [];

  // --------------------------------------------------------------- socket

  function send(message) {
    if (!socket || socket.readyState !== WebSocket.OPEN) return false;
    socket.send(JSON.stringify(message));
    return true;
  }

  function sendToPlugin(payload) {
    return send({
      event: "sendToPlugin",
      action: actionUuid,
      context: inspectorUuid,
      payload,
    });
  }

  function saveGlobalSettings() {
    const port = clampPort(portInput.value);
    const token = tokenInput.value.trim();
    portInput.value = String(port);
    send({
      event: "setGlobalSettings",
      context: pluginUuid,
      payload: { port, token },
    });
    setStatus("saving…", "pending");
    // Give the plugin a moment to adopt the new endpoint before asking it again.
    window.setTimeout(() => requestSounds(), 350);
  }

  function requestSounds() {
    if (!ready) return;
    setStatus("looking for JockeyBox…", "pending");
    if (!sendToPlugin({ event: "getSounds" })) {
      setStatus("plugin is not connected", "bad");
    }
  }

  // ------------------------------------------------------------------ UI

  function setStatus(text, kind) {
    statusLine.textContent = text;
    statusLine.dataset.kind = kind || "idle";
  }

  function clampPort(value) {
    const parsed = Number.parseInt(String(value || "").trim(), 10);
    if (!Number.isFinite(parsed) || parsed < 1 || parsed > 65535) return DEFAULT_PORT;
    return parsed;
  }

  function renderSounds(items, selected) {
    const previous = selected !== undefined ? selected : soundSelect.value;
    soundSelect.innerHTML = "";

    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = items.length ? "— choose a pad —" : "— no pads —";
    soundSelect.appendChild(placeholder);

    let matched = false;
    for (const item of items) {
      const option = document.createElement("option");
      option.value = item.value;
      option.textContent = item.label;
      if (item.value === previous) {
        option.selected = true;
        matched = true;
      }
      soundSelect.appendChild(option);
    }

    if (previous && !matched) {
      // The pad was deleted (or the list came back empty); keep the id visible
      // instead of silently clearing the key's binding.
      const missing = document.createElement("option");
      missing.value = previous;
      missing.textContent = `${previous} (missing)`;
      missing.selected = true;
      soundSelect.appendChild(missing);
    }
  }

  function saveBinding() {
    if (!ready) return;
    send({
      event: "setSettings",
      context: actionUuid,
      payload: { soundId: soundSelect.value || undefined },
    });
  }

  // --------------------------------------------------------------- events

  function onMessage(event) {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }

    switch (message.event) {
      case "didReceiveGlobalSettings": {
        const settings = (message.payload && message.payload.settings) || {};
        const port = Number.isFinite(Number(settings.port)) ? Number(settings.port) : DEFAULT_PORT;
        if (document.activeElement !== portInput) portInput.value = String(port);
        if (document.activeElement !== tokenInput && typeof settings.token === "string") {
          tokenInput.value = settings.token;
        }
        requestSounds();
        return;
      }

      case "didReceiveSettings": {
        const settings = (message.payload && message.payload.settings) || {};
        renderSounds(currentItems, settings.soundId || "");
        return;
      }

      case "sendToPlugin": {
        const payload = message.payload || {};
        if (payload.event === "sounds") {
          currentItems = Array.isArray(payload.items) ? payload.items : [];
          renderSounds(currentItems);
          const portNote = payload.port ? ` on port ${payload.port}` : "";
          if (payload.ok) {
            setStatus(payload.message || "connected", "good");
            hint.textContent = payload.tokenSet
              ? `Token set. Pads come from JockeyBox${portNote}.`
              : `No token set — leave it empty only if JockeyBox has no API token${portNote}.`;
          } else {
            setStatus(payload.message || "JockeyBox is not reachable", "bad");
            hint.textContent = "Start JockeyBox, then press Refresh. Check the port and token if it is already running.";
          }
          return;
        }
        if (payload.event === "connection") {
          setStatus(payload.message || (payload.ok ? "connected" : "failed"), payload.ok ? "good" : "bad");
          if (payload.ok) requestSounds();
          return;
        }
        if (payload.event === "settings") {
          if (document.activeElement !== portInput && payload.port) portInput.value = String(payload.port);
          if (document.activeElement !== tokenInput && typeof payload.token === "string") {
            tokenInput.value = payload.token;
          }
          return;
        }
        return;
      }

      default:
        return;
    }
  }

  // ------------------------------------------------------------- bootstrap

  /** Stream Deck calls this once the inspector is registered. */
  window.connectElgatoStreamDeckSocket = function (inPort, inInspectorUuid, inRegisterEvent, _inInfo, inActionInfo) {
    inspectorUuid = inInspectorUuid;
    socket = new WebSocket(`ws://127.0.0.1:${inPort}`);

    socket.addEventListener("open", () => {
      send({ event: inRegisterEvent, uuid: inspectorUuid });

      let actionInfo = {};
      try {
        actionInfo = JSON.parse(inActionInfo || "{}");
      } catch {
        actionInfo = {};
      }
      actionUuid = actionInfo.context || "";
      pluginUuid = actionInfo.action || "";
      const settings = (actionInfo.payload && actionInfo.payload.settings) || {};
      renderSounds(currentItems, settings.soundId || "");

      // The panic action has nothing to bind; the same inspector serves it so the
      // connection panel is reachable from either action. Match on the action's
      // manifest UUID, not on the instance context (`actionUuid`).
      if (pluginUuid.endsWith(".panic")) {
        soundField.hidden = true;
        el("title").textContent = "JockeyBox panic";
        el("foot").textContent = "Pressing the key stops every pad in JockeyBox.";
      }

      ready = true;
      // Two ways to learn the port/token: Stream Deck's own settings event, and
      // the plugin's copy. Whichever arrives first wins.
      send({ event: "getGlobalSettings", context: pluginUuid });
      sendToPlugin({ event: "hello" });
      setStatus("connecting…", "pending");
      window.setTimeout(requestSounds, 500);
    });

    socket.addEventListener("message", onMessage);
    socket.addEventListener("close", () => {
      ready = false;
      setStatus("Stream Deck closed the inspector socket", "bad");
    });
  };

  // ---------------------------------------------------------------- wiring

  soundSelect.addEventListener("change", saveBinding);

  portInput.addEventListener("change", saveGlobalSettings);
  tokenInput.addEventListener("change", saveGlobalSettings);

  for (const input of [portInput, tokenInput]) {
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        input.blur();
        saveGlobalSettings();
      }
    });
  }

  refreshButton.addEventListener("click", (event) => {
    event.preventDefault();
    requestSounds();
  });

  testButton.addEventListener("click", (event) => {
    event.preventDefault();
    setStatus("testing…", "pending");
    if (!sendToPlugin({ event: "testConnection" })) setStatus("plugin is not connected", "bad");
  });
})();
