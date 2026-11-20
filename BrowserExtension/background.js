// Background: reads PodBridge's local status endpoint. Pages cannot reach that address themselves.
"use strict";

const STATUS_URL = "http://127.0.0.1:47389/status";
const CACHE_MS = 1000;
const OFFLINE = { app: null, streaming: false, latencyMs: 0, device: null };

let cached = { at: 0, value: OFFLINE };

async function readStatus() {
  if (Date.now() - cached.at < CACHE_MS) return cached.value;
  let value = OFFLINE;
  try {
    const response = await fetch(STATUS_URL, { cache: "no-store" });
    if (response.ok) value = await response.json();
  } catch {
    // PodBridge is not running.
  }
  cached = { at: Date.now(), value };
  const { enabled } = await chrome.storage.local.get({ enabled: true });
  await chrome.action.setBadgeText({ text: value.streaming && enabled ? "on" : "" });
  await chrome.action.setBadgeBackgroundColor({ color: "#0067c0" });
  return value;
}

chrome.runtime.onMessage.addListener((message, _sender, reply) => {
  if (message?.type !== "status") return false;
  readStatus().then(reply);
  return true;
});
