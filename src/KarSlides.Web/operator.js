// Operator panel: one card per slot, live through the /ws event stream.
"use strict";

const { api, events, thumbUrl, el, icon, liveStream, t } = KS;

const slots = new Map();      // slot number -> { status, version, view }
let inbox = [];


// ---------- data ----------

function renderProduct(p) {
  if (!p) return;
  const v = document.getElementById("version");
  v.textContent = "v" + p.version;
  v.title = `KarSlides ${p.display}\n${p.copyright}`;
}

async function refreshState() {
  const s = await api("GET", "/api/state");
  renderProduct(s.product);
  renderProblems(s.problems);
  renderVmix(s.vmix);
  for (const st of s.slots) updateSlot(st);
}

async function refreshInbox() {
  try {
    inbox = await api("GET", "/api/files");
  } catch {
    inbox = [];
  }
  for (const s of slots.values()) renderInbox(s);
}

async function refreshClicker() {
  const c = await api("GET", "/api/clicker");
  renderClicker(c.enabled);
}

// ---------- slot card ----------

function updateSlot(st) {
  let s = slots.get(st.slot);
  if (!s) {
    s = { status: st, version: Date.now(), view: buildCard(st.slot) };
    slots.set(st.slot, s);
    document.getElementById("slots").append(s.view.root);
    renderInbox(s);
  }
  const prev = s.status;
  // New thumbnails after load/reload: bump the cache key.
  if (st.state === "ready" && (prev.state !== "ready" || prev.file !== st.file || prev.thumbnails !== st.thumbnails)) {
    s.version = Date.now();
    s.view.strip.dataset.built = "";
  }
  s.status = st;
  renderSlot(s);
}

function buildCard(n) {
  const v = {};
  v.tally = el("span", { class: "chip" });
  v.state = el("span", { class: "chip" });
  v.file = el("span", { class: "file" });
  v.counter = el("div", { class: "counter" });
  v.current = el("img", { alt: "" });
  v.currentEmpty = el("span", { class: "empty" }, t("op.noShow"));
  v.overlay = el("div", { class: "overlay" });
  v.live = el("img", { class: "live", alt: "" });
  v.currentScreen = el("div", { class: "screen" }, v.current, v.live, v.currentEmpty, v.overlay);
  // The thumbnail stays underneath: it shows whenever the live picture is not available.
  v.liveCtl = liveStream(v.live, n, (on) => {
    v.liveOn = on;
    const s = slots.get(n);
    if (s) renderSlot(s);
  });
  v.next = el("img", { alt: "" });
  v.nextEmpty = el("span", { class: "empty" }, "–");
  v.nextTitle = el("h3", {}, t("op.next"));
  v.messages = el("div", { class: "card messages-card" });
  v.strip = el("div", { class: "strip" });
  v.select = el("select", { title: t("op.inboxTitle") });
  v.path = el("input", { type: "text", placeholder: t("op.pathPlaceholder") });
  v.path.addEventListener("keydown", (e) => { if (e.key === "Enter") load(n); });

  const btn = (cls, iconName, label, onclick, title) => {
    const b = el("button", { class: "btn " + cls, onclick });
    if (title) b.title = title;
    if (iconName) b.append(icon(iconName));
    if (label) b.append(label);
    return b;
  };

  v.black = btn("outlined large", "black", "Black", () => run(n, slots.get(n).status.black ? "unblack" : "black"),
    t("op.blackTitle"));
  v.freeze = btn("outlined large", "freeze", "Freeze", () => run(n, slots.get(n).status.freeze ? "unfreeze" : "freeze"),
    t("op.freezeTitle"));

  v.root = el("section", { class: "slot" },
    el("div", { class: "card stage" },
      el("div", { class: "stage-head" },
        el("span", { class: "title" }, t("op.slot", { n })), v.tally, v.state, v.file, v.counter),
      v.currentScreen,
      el("div", { class: "controls" },
        el("div", { class: "btn-group" },
          btn("large icon-only", "first", "", () => run(n, "first"), t("op.first")),
          btn("large", "prev", t("op.prev"), () => run(n, "prev")),
          btn("filled hero", null, t("op.forward"), () => run(n, "next")),
          btn("large icon-only", "last", "", () => run(n, "last"), t("op.last"))),
        el("span", { class: "spacer" }),
        v.black, v.freeze,
        btn("text large", "presenter", t("op.presenter"), () => window.open(`/presenter/${n}${location.search}`, "_blank"),
          t("op.presenterTitle")))),
    el("div", { class: "side" },
      el("div", { class: "card next-card" },
        v.nextTitle,
        el("div", { class: "screen" }, v.next, v.nextEmpty)),
      el("div", { class: "card load-card" },
        el("h3", {}, t("op.presentation")),
        el("div", { class: "row" }, v.select,
          btn("icon-only", "refresh", "", refreshInbox, t("op.refreshInbox"))),
        el("div", { class: "row" }, v.path, btn("filled", "open", t("op.load"), () => load(n))),
        el("div", { class: "row" },
          btn("outlined", "refresh", t("op.reload"), () => run(n, "reload", { onAirCheck: true }),
            t("op.reloadTitle")),
          btn("danger", "eject", t("op.unload"), () => run(n, "unload", { onAirCheck: true })))),
      v.messages),
    v.strip);
  return v;
}

function renderSlot(s) {
  const st = s.status, v = s.view;
  v.root.classList.toggle("program", st.onProgram);
  v.root.classList.toggle("preview", !st.onProgram && st.onPreview);
  v.tally.className = "chip" + (st.onProgram ? " program" : st.onPreview ? " preview" : "");
  v.tally.textContent = t(st.onProgram ? "op.onAir" : st.onPreview ? "op.onPreview" : "op.offAir");
  v.state.textContent = t("state." + st.state);
  v.state.className = "chip" + (st.state === "ready" ? " ready" : st.state === "error" ? " error" : "");
  v.file.textContent = st.file ? st.file.split(/[\\/]/).pop() : "";
  v.file.title = st.file || "";

  const has = st.slideCount > 0;
  const nextSlide = has && st.slide < st.slideCount ? st.slide + 1 : null;
  v.counter.replaceChildren();
  if (has) {
    v.counter.append(el("span", { class: "num" }, String(st.slide)), el("span", { class: "of" }, t("op.of", { n: st.slideCount })));
    if (st.clickCount) v.counter.append(el("span", { class: "step" }, t("op.step", { click: st.click, count: st.clickCount })));
  }
  v.nextTitle.textContent = nextSlide ? t("op.nextSlide", { n: nextSlide }) : t(has ? "op.nextEnd" : "op.next");

  const curUrl = has && st.thumbnails >= st.slide ? thumbUrl(st.slot, st.slide, s.version) : null;
  setShot(v.current, curUrl);
  v.liveCtl.set(st.state !== "empty");
  v.currentEmpty.hidden = !!curUrl || v.liveOn;
  v.currentEmpty.textContent = t(has ? "op.noThumb" : "op.noShow");
  const nextUrl = nextSlide && st.thumbnails >= nextSlide ? thumbUrl(st.slot, nextSlide, s.version) : null;
  setShot(v.next, nextUrl);
  v.nextEmpty.hidden = !!nextUrl;
  v.currentScreen.classList.toggle("black", st.black);
  v.overlay.replaceChildren();
  if (v.liveOn) v.overlay.append(el("span", { class: "chip live-chip", title: t("op.liveTitle") },
    el("span", { class: "dot" }), "LIVE"));
  if (st.black) v.overlay.append(el("span", { class: "chip program" }, "BLACK"));
  if (st.freeze) v.overlay.append(el("span", { class: "chip warn" }, "FREEZE"));

  v.black.classList.toggle("selected", st.black);
  v.freeze.classList.toggle("selected", st.freeze);

  v.messages.replaceChildren(el("h3", {}, t("op.messages")));
  if (st.error) v.messages.append(el("div", { class: "msg error" }, icon("warn"), t("op.error", { message: st.error })));
  for (const w of (st.preflight && st.preflight.warnings) || [])
    v.messages.append(el("div", { class: "msg warning" }, icon("warn"), w));
  if (v.messages.children.length === 1) v.messages.append(el("div", { class: "msg info" }, t("op.allGood")));

  renderStrip(s);
}

function setShot(img, url) {
  if (!url) {
    img.removeAttribute("src");
    return;
  }
  if (img.getAttribute("src") !== url) img.src = url;
}

function renderStrip(s) {
  const st = s.status, strip = s.view.strip;
  const key = `${s.version}:${st.thumbnails}`;
  if (strip.dataset.built !== key) {
    strip.replaceChildren();
    for (let i = 1; i <= st.thumbnails; i++) {
      strip.append(el("button", { "data-slide": i, title: t("op.slideN", { n: i }), onclick: () => run(st.slot, `goto?slide=${i}`) },
        el("img", { src: thumbUrl(st.slot, i, s.version), alt: "", loading: "lazy" }),
        el("span", {}, String(i))));
    }
    strip.dataset.built = key;
  }
  for (const b of strip.children) {
    const cur = Number(b.dataset.slide) === st.slide;
    if (cur && !b.classList.contains("current")) b.scrollIntoView({ block: "nearest", inline: "center" });
    b.classList.toggle("current", cur);
  }
}

function renderInbox(s) {
  const sel = s.view.select;
  const keep = sel.value;
  sel.replaceChildren(el("option", { value: "" }, t(inbox.length ? "op.inboxPick" : "op.inboxEmpty")));
  for (const f of inbox) sel.append(el("option", { value: f.path }, f.name));
  sel.value = keep;
}

// ---------- commands ----------

async function run(n, command, { onAirCheck = false } = {}) {
  let path = `/api/slots/${n}/${command}`;
  try {
    if (onAirCheck && slots.get(n).status.onProgram) {
      if (!confirm(t("op.confirmOnAir", { n, command }))) return;
      path += "?force=true";
    }
    // Navigation and output commands accept GET; load/unload/reload need POST.
    const method = /^(reload|unload)/.test(command) ? "POST" : "GET";
    updateSlot(await api(method, path));
  } catch (e) {
    flash(n, e.message);
  }
}

async function load(n) {
  const s = slots.get(n);
  const path = s.view.path.value.trim() || s.view.select.value;
  if (!path) return flash(n, t("op.pickFile"));
  let url = `/api/slots/${n}/load`;
  if (s.status.onProgram) {
    if (!confirm(t("op.confirmLoad", { n }))) return;
    url += "?force=true";
  }
  try {
    updateSlot(await api("POST", url, { path }));
    s.view.path.value = "";
  } catch (e) {
    flash(n, e.message);
  }
}

let toastTimer = 0;

function flash(n, message) {
  let t = document.querySelector(".toast");
  if (!t) document.body.append(t = el("div", { class: "toast", role: "alert" }));
  t.textContent = KS.t("op.toast", { n, message });
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.remove(), 6000);
}

// ---------- header ----------

function renderProblems(list) {
  list = list || [];
  const chip = document.getElementById("problemsChip");
  const panel = document.getElementById("problems");
  chip.hidden = list.length === 0;
  chip.replaceChildren(icon("warn"), t("op.problems", { n: list.length }));
  panel.replaceChildren(...list.map(p => el("div", {}, p)));
  if (!list.length) panel.classList.remove("open");
}

const settingsLink = document.getElementById("settingsLink");
settingsLink.append(icon("settings"));
settingsLink.href = "/settings" + location.search; // keeps ?token=

document.getElementById("problemsChip").addEventListener("click", () =>
  document.getElementById("problems").classList.toggle("open"));

function renderChip(id, ok, text, title) {
  const b = document.getElementById(id);
  b.className = "chip" + (ok === true ? " ok" : ok === false ? " bad" : "");
  b.replaceChildren(el("span", { class: "dot" }), text);
  b.title = title || "";
}

function renderVmix(v) {
  if (!v) return renderChip("vmix", null, t("op.vmixOff"));
  renderChip("vmix", v.connected, v.connected ? `vMix ${v.version || ""}` : t("op.vmixDown"), v.error || t("op.vmixTitle"));
}

function renderClicker(on) {
  const sw = document.getElementById("clicker");
  sw.classList.toggle("on", on);
  sw.setAttribute("aria-checked", on ? "true" : "false");
  sw.dataset.on = on ? "1" : "";
}

async function toggleClicker() {
  const sw = document.getElementById("clicker");
  const c = await api("POST", "/api/clicker", { enabled: !sw.dataset.on });
  renderClicker(c.enabled);
}

document.getElementById("clicker").addEventListener("click", toggleClicker);
document.getElementById("clicker").addEventListener("keydown", (e) => {
  if (e.key === " " || e.key === "Enter") { e.preventDefault(); toggleClicker(); }
});

// ---------- live ----------

events((type, data) => {
  switch (type) {
    case "state":
      renderProduct(data.product);
      renderProblems(data.problems);
      renderVmix(data.vmix);
      for (const st of data.slots) updateSlot(st);
      break;
    case "slotState":
      updateSlot(data);
      break;
    case "position":
    case "tally": {
      const s = slots.get(data.slot);
      if (s) updateSlot({ ...s.status, ...data });
      break;
    }
    case "clicker":
      renderClicker(data.enabled);
      break;
    case "recovery":
    case "error":
      // slotState follows; problems/vMix are refreshed periodically below
      break;
  }
}, (connected) => {
  renderChip("conn", connected, t(connected ? "op.connected" : "op.disconnected"), t("op.connTitle"));
  if (connected) refreshClicker();
});

refreshState().catch(() => {});
refreshInbox();
// Problems and vMix state are not pushed as events: poll them slowly.
setInterval(() => refreshState().catch(() => {}), 5000);
