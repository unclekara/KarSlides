// Settings page: edits %APPDATA%\KarSlides\settings.json through /api/settings, restarts to apply.
"use strict";

const { api, el, icon, t, lang } = KS;

let fields = [];           // from the server: path, group, label, kind, help, min, max, choices, danger, current, saved
const inputs = new Map();  // path -> { field, read(), row }
let monitors = null;       // GET /api/monitors
let audioDevices = null;   // GET /api/audio/devices

const CONNECTION = {
  virtual: t("st.conn.virtual"), internal: t("st.conn.internal"), hdmi: "HDMI", displayPort: "DisplayPort",
  dvi: "DVI", vga: "VGA", wireless: t("st.conn.wireless"), other: "", unknown: "",
};
const withLang = (path) => path + (path.includes("?") ? "&" : "?") + "lang=" + lang;

// ---------- values ----------

const text = (v) => Array.isArray(v) ? v.join(", ") : v == null ? "" : String(v);

/** Comparable form of a value, so "25" and 25, or "a,b" and ["a","b"] match. */
function canon(kind, v) {
  if (kind === "monitor") return text(Array.isArray(v) ? v[0] : v).trim().toLowerCase();
  if (kind === "list") return (Array.isArray(v) ? v : text(v).split(/[,;\n]/)).map(s => s.trim()).filter(Boolean).join(",");
  if (kind === "int" || kind === "number") return v === "" || v == null ? "" : String(Number(String(v).replace(",", ".")));
  if (kind === "bool") return String(!!v);
  return text(v).trim();
}

function changed(path) {
  const i = inputs.get(path);
  return canon(i.field.kind, i.read()) !== canon(i.field.kind, i.field.saved);
}

// ---------- build ----------

function control(f) {
  switch (f.kind) {
    case "bool": {
      const sw = el("div", { class: "switch", role: "switch", tabindex: "0" }, el("span", { class: "track" }, el("span", { class: "thumb" })));
      const set = (on) => { sw.classList.toggle("on", on); sw.setAttribute("aria-checked", String(on)); };
      set(!!f.saved);
      const toggle = () => { set(!sw.classList.contains("on")); onEdit(); };
      sw.addEventListener("click", toggle);
      sw.addEventListener("keydown", (e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); toggle(); } });
      return { node: sw, read: () => sw.classList.contains("on") };
    }
    case "choice": {
      const sel = el("select", {});
      for (const c of f.choices) sel.append(el("option", { value: c }, c));
      sel.value = text(f.saved);
      sel.addEventListener("change", onEdit);
      return { node: sel, read: () => sel.value };
    }
    case "monitor":
      return monitorControl(f);
    case "audioDevice":
      return audioDeviceControl(f);
    case "int":
    case "number": {
      const inp = el("input", { type: "text", inputmode: "decimal" });
      inp.value = text(f.saved);
      inp.addEventListener("input", onEdit);
      return { node: inp, read: () => inp.value.trim() };
    }
    default: { // text, list
      const inp = el("input", { type: "text", spellcheck: "false" });
      inp.value = text(f.saved);
      if (f.path === "Control.Token") inp.placeholder = t("st.noToken");
      inp.addEventListener("input", onEdit);
      return { node: inp, read: () => inp.value };
    }
  }
}

/** Monitor of the show: the first virtual one, or a connected monitor by its DeviceID. */
function monitorControl(f) {
  const saved = text(Array.isArray(f.saved) ? f.saved[0] : f.saved);
  const sel = el("select", {});
  const info = el("div", { class: "monitor-info" });
  const describe = (m) => [m.name, `${m.width}×${m.height}`, CONNECTION[m.connection], m.isPrimary ? t("st.primary") : ""]
    .filter(Boolean).join(" · ");
  const byValue = new Map();

  function showInfo() {
    const lines = [];
    const m = byValue.get(sel.value);
    if (monitors) {
      const used = monitors.monitors.find(x => x.deviceName === monitors.current.deviceName);
      lines.push(el("div", {}, t("st.showOn"), el("b", {}, used ? `${describe(used)} (${used.deviceName})` : t("st.monitorNotFound"))));
    }
    if (m) for (const n of m.notes) lines.push(el("div", { class: "warn-line" }, n));
    else if (sel.value && sel.value !== "virtual" && monitors)
      lines.push(el("div", { class: "warn-line" }, t("st.monitorMissing")));
    info.replaceChildren(...lines);
  }

  function fill() {
    sel.replaceChildren();
    const virtuals = monitors.monitors.filter(m => m.isVirtual);
    sel.append(el("option", { value: "virtual" },
      t("st.firstVirtual") + (virtuals.length ? ` → ${describe(virtuals[0])}` : t("st.notFoundNow"))));
    byValue.set("virtual", virtuals[0]);
    for (const m of monitors.monitors) {
      const o = el("option", { value: m.value }, describe(m) + (m.suitable ? "" : t("st.unsuitable")));
      if (!m.suitable) o.disabled = true;
      sel.append(o);
      byValue.set(m.value, m);
    }
    const known = [...sel.options].some(o => o.value.toLowerCase() === saved.toLowerCase());
    if (!known && saved) sel.append(el("option", { value: saved }, t("st.custom", { value: saved })));
    sel.value = [...sel.options].find(o => o.value.toLowerCase() === saved.toLowerCase())?.value ?? "virtual";
    showInfo();
    render();
  }

  sel.append(el("option", { value: saved }, saved || "virtual"));
  sel.value = saved;
  sel.addEventListener("change", () => { showInfo(); onEdit(); });
  monitorsLoaded.then(fill).catch(() => info.replaceChildren(t("st.monitorsUnavailable")));
  return {
    node: el("div", { class: "monitor-pick" }, sel, info),
    read: () => sel.value,
    isPrimary: () => !!byValue.get(sel.value)?.isPrimary,
  };
}

/** Where PowerPoint plays sound locally: the Windows default or a device by its id. NDI gets the sound anyway. */
function audioDeviceControl(f) {
  const saved = text(f.saved);
  const sel = el("select", {});
  const info = el("div", { class: "monitor-info" });
  const byId = new Map();

  function showInfo() {
    const lines = [];
    if (audioDevices) {
      const cur = audioDevices.current;
      lines.push(el("div", {}, t("st.now"), el("b", {},
        cur.id ? (cur.name ?? t("st.deviceMissing")) : t("st.windowsDefaultIs", { name: cur.name ?? t("st.noDevices") }))));
    }
    const d = sel.value ? byId.get(sel.value) : audioDevices?.devices.find(x => x.isDefault);
    if (sel.value && !d && audioDevices)
      lines.push(el("div", { class: "warn-line" }, t("st.deviceMissingWarn")));
    if (d?.enhancementsEnabled)
      lines.push(el("div", { class: "warn-line" }, t("st.enhancementsWarn")));
    info.replaceChildren(...lines);
  }

  function fill() {
    sel.replaceChildren();
    const def = audioDevices.devices.find(d => d.isDefault);
    sel.append(el("option", { value: "" }, t("st.windowsDefault") + (def ? ` → ${def.name}` : "")));
    for (const d of audioDevices.devices) {
      sel.append(el("option", { value: d.id }, d.name + (d.isDefault ? t("st.isDefault") : "")));
      byId.set(d.id, d);
    }
    if (saved && !byId.has(saved)) sel.append(el("option", { value: saved }, t("st.notConnected", { value: saved })));
    sel.value = saved;
    showInfo();
    render();
  }

  sel.append(el("option", { value: saved }, saved || t("st.windowsDefault")));
  sel.value = saved;
  sel.addEventListener("change", () => { showInfo(); onEdit(); });
  audioLoaded.then(fill).catch(() => info.replaceChildren(t("st.devicesUnavailable")));
  return { node: el("div", { class: "monitor-pick" }, sel, info), read: () => sel.value };
}

let audioLoaded = Promise.resolve();
let monitorsLoaded = Promise.resolve();

/** Text with http(s) links made clickable (help texts: the NDI license asks for a link to ndi.video). */
function linkify(s) {
  return s.split(/(https?:\/\/[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:\/\S*)?)/i).map((part, i) => i % 2
    ? el("a", { href: part.replace(/[.,;]$/, ""), target: "_blank", rel: "noopener" }, part.replace(/[.,;]$/, ""))
    : part);
}

function build() {
  const root = document.getElementById("groups");
  root.replaceChildren();
  inputs.clear();
  const groups = new Map();
  for (const f of fields) {
    if (!groups.has(f.group)) groups.set(f.group, []);
    groups.get(f.group).push(f);
  }
  for (const [name, list] of groups) {
    const card = el("section", { class: "card settings-group" }, el("h3", {}, name));
    for (const f of list) {
      const c = control(f);
      const range = f.min != null && f.max != null ? ` (${f.min}…${f.max})` : "";
      const note = el("div", { class: "pending", hidden: "" }, t("st.pending"));
      const row = el("div", { class: "setting" + (f.danger ? " danger" : "") + (f.kind === "monitor" || f.kind === "audioDevice" ? " wide" : "") },
        el("div", { class: "setting-text" },
          el("label", {}, f.label + range),
          f.help ? el("div", { class: "help" }, ...linkify(f.help)) : "",
          note),
        c.node);
      inputs.set(f.path, { field: f, read: c.read, row, note, isPrimary: c.isPrimary });
      card.append(row);
    }
    root.append(card);
  }
  render();
}

function render() {
  let dirty = 0;
  for (const [path, i] of inputs) {
    const d = changed(path);
    dirty += d ? 1 : 0;
    i.row.classList.toggle("dirty", d);
    // Saved but not yet in effect.
    i.note.hidden = canon(i.field.kind, i.field.saved) === canon(i.field.kind, i.field.current);
  }
  const save = document.getElementById("save");
  save.disabled = dirty === 0;
  save.replaceChildren(icon("save"), dirty ? t("st.save", { n: dirty }) : t("st.saved"));
  const pending = fields.some(f => canon(f.kind, f.saved) !== canon(f.kind, f.current));
  document.getElementById("restartNote").hidden = !pending;
  document.getElementById("restart").classList.toggle("filled", pending);
  document.getElementById("restart").classList.toggle("outlined", !pending);
}

function onEdit() {
  render();
}

// ---------- actions ----------

async function load() {
  monitorsLoaded = api("GET", withLang("/api/monitors")).then(m => { monitors = m; });
  audioLoaded = api("GET", "/api/audio/devices").then(a => { audioDevices = a; });
  const s = await api("GET", withLang("/api/settings"));
  fields = s.fields;
  document.getElementById("file").textContent = s.file;
  const p = s.product;
  const v = document.getElementById("version");
  v.textContent = "KarSlides " + p.display;
  v.title = p.copyright;
  document.getElementById("about").replaceChildren(
    el("b", {}, `KarSlides ${p.display}`), ` · ${p.author} · `,
    el("a", { href: `mailto:${p.email}` }, p.email),
    el("div", {}, `${p.ndiNotice} · `, el("a", { href: p.ndiUrl, target: "_blank", rel: "noopener" }, p.ndiUrl)));
  build();
}

async function save() {
  const values = {};
  for (const [path, i] of inputs) if (changed(path)) values[path] = i.read();
  if (!Object.keys(values).length) return;
  const danger = Object.keys(values).filter(p => inputs.get(p).field.danger);
  if (danger.length && !confirm(t("st.confirmDanger"))) return;
  const mon = inputs.get("Slots.Monitors");
  if ("Slots.Monitors" in values && mon?.isPrimary?.()
      && !confirm(t("st.confirmPrimary"))) return;
  try {
    const s = await api("PUT", withLang("/api/settings"), { values });
    fields = s.fields;
    for (const f of fields) inputs.get(f.path).field = f;
    render();
    toast(t("st.savedToast"));
  } catch (e) {
    toast(e.message, true);
  }
}

/** Where the panel will be after the restart: the saved port and token. */
function nextUrl() {
  const get = (p) => fields.find(f => f.path === p)?.saved;
  const port = get("Control.Port") || location.port;
  const token = get("Control.Token");
  return `${location.protocol}//${location.hostname}:${port}/settings` + (token ? `?token=${encodeURIComponent(token)}` : "");
}

async function restart() {
  if (inputs.size && [...inputs.keys()].some(changed)
      && !confirm(t("st.confirmUnsaved"))) return;
  let force = false;
  try {
    await api("POST", "/api/restart");
  } catch (e) {
    if (e.status !== 409) return toast(e.message, true);
    if (!confirm(t("st.confirmOnAir"))) return;
    force = true;
    try { await api("POST", "/api/restart?force=true"); } catch (e2) { return toast(e2.message, true); }
  }
  const target = nextUrl();
  document.getElementById("restarting").hidden = false;
  const started = Date.now();
  await new Promise(r => setTimeout(r, 3000));
  // Wait until the new instance answers, then open the settings page there.
  for (;;) {
    try {
      const probe = new URL(target);
      probe.pathname = "/api/state";
      const r = await fetch(probe, { cache: "no-store" });
      if (r.ok) break;
    } catch { /* still restarting */ }
    const s = Math.round((Date.now() - started) / 1000);
    document.getElementById("restartingText").textContent = t("st.waiting", { s }) + (force ? t("st.waitingFrozen") : "");
    await new Promise(r => setTimeout(r, 1000));
  }
  location.href = target;
}

let toastTimer = 0;
function toast(message, error = false) {
  let t = document.querySelector(".toast");
  if (!t) document.body.append(t = el("div", { class: "toast", role: "alert" }));
  t.textContent = message;
  t.classList.toggle("error", error);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.remove(), error ? 9000 : 5000);
}

// ---------- init ----------

const back = document.getElementById("back");
back.append(icon("back"));
back.href = "/" + location.search;
document.getElementById("restart").append(icon("refresh"), t("st.restart"));
document.getElementById("save").addEventListener("click", save);
document.getElementById("restart").addEventListener("click", restart);
window.addEventListener("beforeunload", (e) => {
  if ([...inputs.keys()].some(changed)) e.preventDefault();
});
load().catch(e => toast(t("st.loadFailed", { message: e.message }), true));
