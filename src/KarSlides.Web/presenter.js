// Presenter page /presenter/{n}: current and next slide, notes, "X of Y", talk timer and clock.
// Read-only: the speaker drives slides with the clicker; this page never sends navigation commands.
"use strict";

const { api, events, thumbUrl, el, icon, liveStream, t, lang } = KS;

const slot = Number(location.pathname.split("/").filter(Boolean)[1] || 1);
let status = null;
let notes = [];
let version = Date.now();
// Live picture of the show over the thumbnail of the current slide.
const live = liveStream(document.getElementById("live"), slot);

async function refreshNotes() {
  try {
    notes = (await api("GET", `/api/slots/${slot}/notes`)).notes || [];
  } catch {
    notes = [];
  }
  render();
}

function update(st) {
  const prev = status;
  status = st;
  if (!prev || prev.file !== st.file || (st.state === "ready" && prev.state !== "ready") || prev.thumbnails !== st.thumbnails) {
    version = Date.now();
    refreshNotes();
    return;
  }
  render();
}

function render() {
  const st = status;
  const has = st && st.slideCount > 0;
  const counter = document.getElementById("counter");
  if (has) counter.replaceChildren(t("pr.slide"), el("b", {}, String(st.slide)), t("pr.of", { n: st.slideCount }));
  else counter.textContent = t("pr.noShow");
  document.getElementById("state").textContent =
    !st ? "" : st.black ? t("pr.black") : st.freeze ? t("pr.frozen") : st.clickCount ? t("pr.step", { click: st.click, count: st.clickCount }) : "";

  setShot("current", has && st.thumbnails >= st.slide ? thumbUrl(slot, st.slide, version) : null);
  live.set(!!st && st.state !== "empty");
  const next = has && st.slide < st.slideCount ? st.slide + 1 : null;
  setShot("next", next && st.thumbnails >= next ? thumbUrl(slot, next, version) : null);
  document.getElementById("nextNotes").textContent = next ? (notes[next - 1] || "").split("\n")[0] : t("pr.lastSlide");

  const text = has ? (notes[st.slide - 1] || "") : "";
  const box = document.getElementById("notes");
  box.textContent = text || t("pr.noNotes");
  box.classList.toggle("empty", !text);
}

function setShot(id, url) {
  const img = document.getElementById(id);
  if (!url) img.removeAttribute("src");
  else if (img.getAttribute("src") !== url) img.src = url;
}

// ---------- timer and clock (local to this page) ----------

let started = null, elapsed = 0;

function fmt(ms) {
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const mm = String(m).padStart(2, "0"), ss = String(sec).padStart(2, "0");
  return h ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

function tick() {
  const now = Date.now();
  const total = elapsed + (started ? now - started : 0);
  const t = document.getElementById("timer");
  t.textContent = fmt(total);
  t.classList.toggle("running", !!started);
  document.getElementById("clock").textContent =
    new Date().toLocaleTimeString(lang === "ru" ? "ru-RU" : "en-GB", { hour: "2-digit", minute: "2-digit" });
}

function renderTimerButton() {
  document.getElementById("timerToggle").replaceChildren(...(started ? [icon("pause"), t("pr.pause")] : [icon("play"), t("pr.start")]));
}

document.getElementById("timerToggle").addEventListener("click", () => {
  if (started) {
    elapsed += Date.now() - started;
    started = null;
  } else {
    started = Date.now();
  }
  renderTimerButton();
  tick();
});
document.getElementById("timerReset").replaceChildren(icon("reset"), t("pr.reset"));
renderTimerButton();

document.getElementById("timerReset").addEventListener("click", () => {
  elapsed = 0;
  if (started) started = Date.now();
  tick();
});

setInterval(tick, 250);
tick();

// ---------- live ----------

events((type, data) => {
  if (type === "state") {
    const st = data.slots.find(s => s.slot === slot);
    if (st) update(st);
  } else if (type === "slotState" && data.slot === slot) {
    update(data);
  } else if (type === "position" && data.slot === slot && status) {
    update({ ...status, ...data });
  }
}, () => {});

// Show the slot right away, before the event stream delivers its first state.
api("GET", `/api/slots/${slot}`).then(update).catch(() => {});
