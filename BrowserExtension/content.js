// Page side: gets PodBridge's status from the background and applies the delay to the page's videos.
"use strict";

(() => {
  const POLL_MS = 2000;
  const manager = new globalThis.PodBridgeDelay.VideoDelayManager();
  let settings = { enabled: true, offsetMs: 0 };
  let status = { streaming: false, latencyMs: 0 };

  const apply = () => {
    const delay = settings.enabled && status.streaming
      ? Math.max(0, status.latencyMs + settings.offsetMs)
      : 0;
    manager.setDelay(delay);
  };

  const poll = async () => {
    // A hidden tab draws no frames even if video plays, so there is nothing to do.
    if (document.hidden && manager.activeCount === 0) return;
    try {
      status = await chrome.runtime.sendMessage({ type: "status" }) ?? status;
    } catch {
      // After an extension update, scripts in old pages lose their connection; stop delaying.
      status = { streaming: false, latencyMs: 0 };
    }
    apply();
  };

  chrome.storage.local.get({ enabled: true, offsetMs: 0 }).then((stored) => {
    settings = stored;
    poll();
  });
  chrome.storage.onChanged.addListener((changes) => {
    for (const [key, change] of Object.entries(changes)) settings[key] = change.newValue;
    apply();
  });

  // Pick up new videos and fullscreen changes right away.
  document.addEventListener("play", apply, true);
  document.addEventListener("loadedmetadata", apply, true);
  document.addEventListener("fullscreenchange", apply);
  setInterval(poll, POLL_MS);
})();
