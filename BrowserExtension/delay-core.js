// Shows a <video> element's picture a given amount of time late.
// An opaque canvas is placed over the video; frames are copied, queued and drawn on the canvas when they are due.
// Audio is left alone and reaches the HomePod with PodBridge's latency; the picture catches up with it.
"use strict";

class VideoDelayer {
  static MAX_QUEUED_FRAMES = 240;

  // Tied to display refresh in real use; the test page swaps it so it can run in a window that is not on screen.
  static schedule = (callback) => requestAnimationFrame(callback);
  static unschedule = (handle) => cancelAnimationFrame(handle);

  constructor(video) {
    this.video = video;
    this.delayMs = 0;
    this.queue = [];
    this.canvas = null;
    this.context = null;
    this.shownFrame = null;
    this.drawHandle = 0;
    this.capturing = false;
    this.lastCaptureTime = -1;
    this.unsupported = false;
  }

  get active() {
    return this.canvas !== null;
  }

  setDelay(delayMs) {
    this.delayMs = delayMs;
    const wanted = delayMs > 0 && this.canDelay();
    if (wanted && !this.active) this.start();
    else if (!wanted && this.active) this.stop();
  }

  // DRM video frames cannot be copied; when the video element itself is fullscreen nothing can be drawn over it.
  canDelay() {
    const video = this.video;
    return !this.unsupported
      && video.isConnected
      && !video.mediaKeys
      && document.fullscreenElement !== video
      && document.pictureInPictureElement !== video
      && video.videoWidth > 0
      && video.offsetWidth > 0;
  }

  start() {
    const canvas = document.createElement("canvas");
    canvas.dataset.podbridge = "delay";
    Object.assign(canvas.style, { position: "absolute", pointerEvents: "none", margin: "0", padding: "0", border: "0" });
    this.video.after(canvas);
    this.canvas = canvas;
    this.context = canvas.getContext("2d", { alpha: false });
    this.layout();

    // Avoid a blank picture until the first frames are due. The video is not made transparent; the opaque canvas covers it,
    // because browsers stop presenting frames of an invisible video.
    this.paint(this.video);

    this.lastCaptureTime = -1;
    this.drawHandle = VideoDelayer.schedule(() => this.onDraw());
  }

  stop() {
    if (!this.active) return;
    VideoDelayer.unschedule(this.drawHandle);
    this.canvas.remove();
    this.canvas = null;
    this.context = null;
    for (const frame of this.queue) frame.bitmap.close();
    this.queue = [];
    this.shownFrame?.close();
    this.shownFrame = null;
  }

  // Frames are captured in the draw loop whenever the video has advanced. We do not rely on
  // requestVideoFrameCallback: some browsers lack it and in some situations its callbacks stop.
  async capture(now) {
    const time = this.video.currentTime;
    if (this.capturing || time === this.lastCaptureTime) return;
    this.capturing = true;
    this.lastCaptureTime = time;
    try {
      const bitmap = await createImageBitmap(this.video, {
        resizeWidth: this.canvas.width,
        resizeHeight: this.canvas.height,
        resizeQuality: "low",
      });
      if (!this.active) {
        bitmap.close();
        return;
      }
      this.queue.push({ bitmap, due: now + this.delayMs });
      while (this.queue.length > VideoDelayer.MAX_QUEUED_FRAMES) this.queue.shift().bitmap.close();
    } catch (error) {
      // Protected or inaccessible source: fall back to normal playback without delay.
      this.unsupported = true;
      this.stop();
    } finally {
      this.capturing = false;
    }
  }

  onDraw() {
    if (!this.active) return;
    this.drawHandle = VideoDelayer.schedule(() => this.onDraw());
    if (!this.canDelay()) {
      this.stop();
      return;
    }
    this.layout();

    const now = performance.now();
    this.capture(now);
    let next = null;
    while (this.queue.length > 0 && this.queue[0].due <= now) {
      next?.bitmap.close();
      next = this.queue.shift();
    }
    if (next) {
      this.shownFrame?.close();
      this.shownFrame = next.bitmap;
      this.paint(next.bitmap);
    }
  }

  // Fits the canvas to the video's box; queued frames still draw correctly after a resize.
  layout() {
    const video = this.video;
    const canvas = this.canvas;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(video.offsetWidth * ratio));
    const height = Math.max(1, Math.round(video.offsetHeight * ratio));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      if (this.shownFrame) this.paint(this.shownFrame);
    }
    const style = canvas.style;
    const left = `${video.offsetLeft}px`;
    const top = `${video.offsetTop}px`;
    if (style.left !== left) style.left = left;
    if (style.top !== top) style.top = top;
    style.width = `${video.offsetWidth}px`;
    style.height = `${video.offsetHeight}px`;
    const computed = getComputedStyle(video);
    // The canvas comes after the video in the DOM, so with the same z-index it paints on top.
    if (style.zIndex !== computed.zIndex) style.zIndex = computed.zIndex;
    if (style.transform !== computed.transform) style.transform = computed.transform === "none" ? "" : computed.transform;
  }

  // Mimics object-fit; the default "contain" letterboxes with black bars.
  paint(source) {
    const canvas = this.canvas;
    const context = this.context;
    const sourceWidth = source.videoWidth ?? source.width;
    const sourceHeight = source.videoHeight ?? source.height;
    if (!sourceWidth || !sourceHeight) return;

    context.fillStyle = "#000";
    context.fillRect(0, 0, canvas.width, canvas.height);
    const fit = getComputedStyle(this.video).objectFit;
    // Queued frames are already scaled to the canvas, so the aspect ratio comes from the video.
    const aspect = (this.video.videoWidth || sourceWidth) / (this.video.videoHeight || sourceHeight);
    let width = canvas.width;
    let height = canvas.height;
    if (fit !== "fill") {
      const scaleByWidth = canvas.width / aspect <= canvas.height;
      const useWidth = fit === "cover" ? !scaleByWidth : scaleByWidth;
      if (useWidth) height = canvas.width / aspect;
      else width = canvas.height * aspect;
    }
    context.drawImage(source, (canvas.width - width) / 2, (canvas.height - height) / 2, width, height);
  }
}

// Tracks the page's videos and applies delay changes to all of them.
class VideoDelayManager {
  constructor() {
    this.delayers = new Map();
    this.delayMs = 0;
  }

  setDelay(delayMs) {
    this.delayMs = delayMs;
    this.refresh();
  }

  refresh() {
    const videos = new Set(document.querySelectorAll("video"));
    for (const [video, delayer] of this.delayers) {
      if (!videos.has(video) || !video.isConnected) {
        delayer.stop();
        this.delayers.delete(video);
      }
    }
    for (const video of videos) {
      let delayer = this.delayers.get(video);
      if (!delayer) {
        delayer = new VideoDelayer(video);
        this.delayers.set(video, delayer);
      }
      delayer.setDelay(this.delayMs);
    }
  }

  get activeCount() {
    let count = 0;
    for (const delayer of this.delayers.values()) if (delayer.active) count++;
    return count;
  }
}

globalThis.PodBridgeDelay = { VideoDelayer, VideoDelayManager };
