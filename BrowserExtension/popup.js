"use strict";

const LOCAL_ORIGIN = { origins: ["http://127.0.0.1:47389/*"] };
// Firefox also asks before content scripts may run on sites; Chromium grants that at install time.
const REQUESTED_ORIGINS = { origins: ["http://127.0.0.1:47389/*", "<all_urls>"] };

const message = (key, ...substitutions) => chrome.i18n.getMessage(key, substitutions.map(String));

const statusText = document.getElementById("status");
const enabled = document.getElementById("enabled");
const offset = document.getElementById("offset");
const offsetLabel = document.getElementById("offsetLabel");
const grant = document.getElementById("grant");

document.documentElement.lang = chrome.i18n.getUILanguage();
for (const element of document.querySelectorAll("[data-i18n]")) {
  element.textContent = message(element.dataset.i18n);
}
offset.setAttribute("aria-label", message("fineTune"));

const formatOffset = (value) => `${value > 0 ? "+" : ""}${value} ms`;

async function render() {
  const settings = await chrome.storage.local.get({ enabled: true, offsetMs: 0 });
  enabled.checked = settings.enabled;
  offset.value = settings.offsetMs;
  offsetLabel.textContent = formatOffset(settings.offsetMs);

  const granted = await chrome.permissions.contains(LOCAL_ORIGIN);
  grant.hidden = granted;

  const status = await chrome.runtime.sendMessage({ type: "status" });
  statusText.classList.toggle("on", Boolean(status?.streaming));
  const device = status?.device ?? "HomePod";
  if (!granted) {
    statusText.textContent = message("needAccess");
  } else if (!status?.app) {
    statusText.textContent = message("notRunning");
  } else if (!status.streaming) {
    statusText.textContent = message("notStreaming");
  } else if (settings.enabled) {
    statusText.textContent = message("delaying", device, Math.max(0, status.latencyMs + settings.offsetMs));
  } else {
    statusText.textContent = message("syncOff", device);
  }
}

enabled.addEventListener("change", async () => {
  await chrome.storage.local.set({ enabled: enabled.checked });
  render();
});

offset.addEventListener("input", () => {
  offsetLabel.textContent = formatOffset(Number(offset.value));
});
offset.addEventListener("change", async () => {
  await chrome.storage.local.set({ offsetMs: Number(offset.value) });
  render();
});

grant.addEventListener("click", async () => {
  await chrome.permissions.request(REQUESTED_ORIGINS);
  render();
});

render();
