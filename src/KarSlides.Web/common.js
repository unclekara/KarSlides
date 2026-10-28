// Shared helpers for the operator panel and the presenter page: API calls and the event stream.
// The access token (if the server requires one) is taken from the page URL: /?token=... or /presenter/1?token=...
"use strict";

const KS = (() => {
  const token = new URLSearchParams(location.search).get("token");

  function withToken(url) {
    if (!token) return url;
    return url + (url.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(token);
  }

  async function api(method, path, body) {
    const res = await fetch(withToken(path), {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    const data = text ? JSON.parse(text) : null;
    if (!res.ok) {
      const err = new Error((data && data.error) || `${res.status} ${res.statusText}`);
      err.status = res.status;
      throw err;
    }
    return data;
  }

  /** Opens /ws and keeps it open; onEvent(type, data) for every message, onStatus(bool) on (dis)connect. */
  function events(onEvent, onStatus) {
    let delay = 500;
    function connect() {
      const proto = location.protocol === "https:" ? "wss:" : "ws:";
      const ws = new WebSocket(withToken(`${proto}//${location.host}/ws`));
      ws.onopen = () => { delay = 500; onStatus(true); };
      ws.onmessage = (m) => {
        try {
          const e = JSON.parse(m.data);
          onEvent(e.type, e.data);
        } catch (err) {
          console.error("bad event", err, m.data);
        }
      };
      ws.onclose = () => {
        onStatus(false);
        setTimeout(connect, delay);
        delay = Math.min(delay * 2, 5000);
      };
    }
    connect();
  }

  function thumbUrl(slot, slide, version) {
    return withToken(`/api/slots/${slot}/thumb/${slide}.png?v=${encodeURIComponent(version)}`);
  }

  function el(tag, attrs = {}, ...children) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "class") e.className = v;
      else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
      else e.setAttribute(k, v);
    }
    for (const c of children) e.append(c);
    return e;
  }

  const BLANK = "data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

  /**
   * Live MJPEG of what goes on air in an <img>: class "on" while pictures arrive. Retries every 5 s
   * after an error (the thumbnail underneath stays visible meanwhile) and stops while the tab is hidden.
   * Returns { set(wanted) }; onState(bool) reports when the live picture appears or goes.
   */
  function liveStream(img, slot, onState = () => {}) {
    let wanted = false, timer = 0;
    function stop() {
      if (!img.dataset.live) return;
      img.dataset.live = "";
      img.src = BLANK; // dropping the src alone does not close the stream in every browser
      img.classList.remove("on");
      onState(false);
    }
    function apply() {
      clearTimeout(timer);
      if (wanted && !document.hidden) {
        if (img.dataset.live) return;
        img.dataset.live = "1";
        img.src = withToken(`/api/slots/${slot}/live.mjpg?t=${Date.now()}`);
      } else {
        stop();
      }
    }
    img.addEventListener("load", () => {
      if (img.dataset.live && !img.classList.contains("on")) {
        img.classList.add("on");
        onState(true);
      }
    });
    img.addEventListener("error", () => {
      if (!img.dataset.live) return;
      stop();
      timer = setTimeout(apply, 5000);
    });
    document.addEventListener("visibilitychange", apply);
    return { set(on) { if (wanted !== on) { wanted = on; apply(); } } };
  }

  // Material Symbols paths (24px grid), inlined so the panel works offline.
  const ICONS = {
    first: "M18.41 16.59 13.82 12l4.59-4.59L17 6l-6 6 6 6zM6 6h2v12H6z",
    prev: "M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z",
    next: "M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z",
    last: "M5.59 7.41 10.18 12l-4.59 4.59L7 18l6-6-6-6zM16 6h2v12h-2z",
    refresh: "M17.65 6.35A7.96 7.96 0 0 0 12 4a8 8 0 1 0 7.73 10h-2.08A6 6 0 1 1 12 6c1.66 0 3.14.69 4.22 1.78L13 11h7V4z",
    black: "M3 5h18v14H3z",
    freeze: "M6 19h4V5H6zm8-14v14h4V5z",
    presenter: "M20 3H4a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h4v2h8v-2h4a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2m0 13H4V5h16z",
    open: "M20 6h-8l-2-2H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2m0 12H4V8h16z",
    eject: "M5 17h14v2H5zm7-12-6.67 10h13.34z",
    warn: "M1 21h22L12 2zm12-3h-2v-2h2zm0-4h-2v-4h2z",
    play: "M8 5v14l11-7z",
    pause: "M6 19h4V5H6zm8-14v14h4V5z",
    reset: "M12 5V1L7 6l5 5V7a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8",
    settings: "M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96a7 7 0 0 0-1.62-.94l-.36-2.54a.48.48 0 0 0-.48-.41h-3.84a.47.47 0 0 0-.47.41l-.36 2.54a7.4 7.4 0 0 0-1.62.94l-2.39-.96a.48.48 0 0 0-.59.22L2.74 8.87a.47.47 0 0 0 .12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54a7 7 0 0 0 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32a.48.48 0 0 0-.12-.61zM12 15.6a3.6 3.6 0 1 1 0-7.2 3.6 3.6 0 0 1 0 7.2",
    back: "M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20z",
    save: "M17 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7zm-5 16a3 3 0 1 1 0-6 3 3 0 0 1 0 6m3-10H5V5h10z",
  };

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
    p.setAttribute("d", ICONS[name]);
    svg.append(p);
    return svg;
  }

  return { api, events, thumbUrl, el, icon, liveStream };
})();
