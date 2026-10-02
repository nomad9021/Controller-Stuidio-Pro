"use strict";

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const params = new URLSearchParams(location.search);
if (params.get("hc") === "1") document.documentElement.classList.add("opaque");
if (params.get("rm") === "1") document.documentElement.classList.add("reduce-motion");

const SHAPES = {
  linear: [0, 3, 3, 3, 3, 3, 3, 3, 3, 3],
  progressive: [0, 0, 1, 2, 2, 3, 4, 5, 5, 6],
  late: [0, 0, 0, 0, 1, 8, 8, 8, 8, 8],
  early: [0, 2, 6, 8, 8, 8, 8, 8, 8, 8],
};
const BUTTON_NAMES = {
  "": "None", cross: "Cross", circle: "Circle", square: "Square", triangle: "Triangle",
  l1: "L1", r1: "R1", l2: "L2", r2: "R2", l3: "L3", r3: "R3", create: "Create", options: "Options",
  ps: "PS", touchpad: "Touchpad", mute: "Mute", up: "D-pad Up", down: "D-pad Down",
  left: "D-pad Left", right: "D-pad Right", paddle_left: "Back Left", paddle_right: "Back Right",
  fn_left: "Fn Left", fn_right: "Fn Right",
};
const REACTION_BUTTONS = ["cross", "circle", "square", "triangle", "l1", "r1", "l3", "r3", "up", "down",
  "left", "right", "paddle_left", "paddle_right", "fn_left", "fn_right", "create", "options", "touchpad"];
const MAP_TARGETS = ["", "cross", "circle", "square", "triangle", "l1", "r1", "l3", "r3", "create", "options",
  "up", "down", "left", "right"];
const LIGHT_COLORS = ["#ff3b30", "#ff5a1f", "#ff9f0a", "#ffd60a", "#34c759", "#00c7be", "#32ade6",
  "#2f6bff", "#5e5ce6", "#bf5af2", "#ff2d92", "#ffffff"];
const WHEN_TEXT = {
  buttons: r => r.buttons.length ? `${r.buttons.map(b => BUTTON_NAMES[b]).join(" or ")} pressed` : "A button is pressed",
  slam: () => "Slammed", held: r => `Held past ${r.held.above}%`, gear: () => "Gear changes",
  abs: () => "Wheels lock up", wheelspin: () => "Wheelspin", redline: () => "Near the redline",
  rumble: r => `Game rumbles over ${(r.rumble || {}).above ?? 30}%`,
};
const INSTANT = new Set(["buttons", "gear", "slam"]);
const FMT = {
  pct: v => v + "%", zone: v => v * 10 + "%", ms: v => v + " ms", hz: v => v + " Hz",
  glass: v => (v < 40 ? "Clear" : v < 70 ? "Balanced" : "Frosted"),
  curve: v => (v == 0 ? "Linear" : v > 0 ? `Gentle ${v}` : `Quick ${-v}`),
};

let status = null, live = null, view = null, draft = null, saveTimer = null;
let tabs = { l2: "feel", r2: "feel" };
const openReactions = new Set();

// ------------------------------------------------------------------ helpers

async function api(method, path, body) {
  const res = await fetch(path, {
    method, headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}
const getPath = (o, p) => p.split(".").reduce((x, k) => (x == null ? x : x[k]), o);
function setPath(o, p, v) {
  const ks = p.split(".");
  for (const k of ks.slice(0, -1)) o = o[k] ??= {};
  o[ks.at(-1)] = v;
}
function showOutput(input) {
  const out = input.parentElement.querySelector("output");
  if (out) out.textContent = (FMT[input.dataset.fmt] || String)(Number(input.value));
}
function segKeys(e) {
  if (!["ArrowLeft", "ArrowRight"].includes(e.key)) return;
  const btns = $$("button", e.currentTarget);
  const i = btns.findIndex(b => b.getAttribute("aria-checked") === "true" || b.getAttribute("aria-selected") === "true");
  const next = btns[(i + (e.key === "ArrowRight" ? 1 : -1) + btns.length) % btns.length];
  next.click(); next.focus(); e.preventDefault();
}
/** Show/hide elements carrying `attr` like "feel.type=zones|smooth" or "path". */
function applyShow(root, attr, obj) {
  $$(`[${attr}]`, root).forEach(el => {
    const [path, vals] = el.getAttribute(attr).split("=");
    const v = getPath(obj, path);
    el.hidden = vals ? !vals.split("|").includes(String(v)) : !v;
  });
}
/** Wire every element with data-<key> inside root to obj; onChange(path, value, structural). */
function bind(root, key, obj, onChange) {
  $$(`[data-${key}]`, root).forEach(el => {
    const path = el.dataset[key];
    const value = getPath(obj, path);
    if (el.classList.contains("seg")) {
      $$("button", el).forEach(b => {
        const on = b.dataset.value === String(value);
        b.setAttribute("aria-checked", String(on));
        b.tabIndex = on ? 0 : -1;
        b.onclick = () => onChange(path, b.dataset.value, true);
      });
      el.onkeydown = segKeys;
    } else if (el.type === "checkbox") {
      el.checked = !!value;
      el.onchange = () => onChange(path, el.checked, true);
    } else if (el.type === "range") {
      el.value = value;
      showOutput(el);
      el.oninput = () => { showOutput(el); onChange(path, Number(el.value), false); };
    } else if (el.tagName === "SELECT") {
      el.value = value ?? "";
      el.onchange = () => onChange(path, el.value, true);
    } else if (el.type === "color") {
      el.value = value || "#ffffff";
      el.oninput = () => onChange(path, el.value, false);
    }
  });
}
function queueSave() { clearTimeout(saveTimer); saveTimer = setTimeout(save, 250); }

// ------------------------------------------------------------------ navigation

function renderSidebar() {
  $("#preset-list").replaceChildren(...status.presets.map(p => {
    const li = document.createElement("li");
    const b = document.createElement("button");
    b.type = "button"; b.className = "nav-row"; b.dataset.view = "preset:" + p.id;
    b.innerHTML = `<i class="swatch-dot" aria-hidden="true"></i><span></span>`;
    b.querySelector("i").style.setProperty("--c", p.lightbar || "#888");
    b.querySelector("span").textContent = p.name;
    if (p.id === status.active) {
      const badge = document.createElement("span");
      badge.className = "nav-badge"; badge.textContent = "In Use";
      b.append(badge);
    }
    li.append(b);
    return li;
  }));
  $$(".nav-row").forEach(b => {
    b.onclick = () => select(b.dataset.view);
    if (b.dataset.view === view) b.setAttribute("aria-current", "page");
    else b.removeAttribute("aria-current");
  });
}

const PAGES = {
  lighting: ["Lighting", "Your light bar and player lights, kept the way you set them."],
  output: ["Output", "What games receive from your controller."],
  tester: ["Controller Tester", "Everything the controller reports, live. Preset effects pause while the Trigger Lab runs."],
};

function select(v) {
  view = v;
  try { localStorage.setItem("view", v); } catch {}
  const page = v.startsWith("preset:") ? "preset" : v;
  for (const id of ["preset", "lighting", "output", "tester"]) $("#view-" + id).hidden = id !== page;
  $("#preset-actions").hidden = page !== "preset";
  if (page === "preset") {
    const p = status.presets.find(p => "preset:" + p.id === v) || status.presets[0];
    view = "preset:" + p.id;
    draft = structuredClone(p);
    if (draft.telemetry === "forza") draft.telemetry = "game";
    buildTriggers();
    renderPreset();
  } else {
    draft = null;
    $("#title").textContent = PAGES[page][0];
    $("#tb-sub").textContent = PAGES[page][0];
    $("#subtitle").textContent = PAGES[page][1];
    if (page === "lighting") renderLighting();
    if (page === "output") renderOutput();
  }
  renderSidebar();
  $("#content").scrollTop = 0;
}

// ------------------------------------------------------------------ preset editor

function buildTriggers() {
  const grid = $("#triggers");
  grid.replaceChildren();
  for (const side of ["l2", "r2"]) {
    const card = $("#tpl-trigger").content.firstElementChild.cloneNode(true);
    card.dataset.side = side;
    $('[data-t="name"]', card).textContent = side.toUpperCase();
    $$("[data-tab]", card).forEach(b => {
      b.onclick = () => { tabs[side] = b.dataset.tab; renderTrigger(side); };
    });
    $(".tabs", card).onkeydown = segKeys;
    const prof = $(".profile", card);
    buildProfile(prof, side);
    $$("[data-shape]", card).forEach(b => (b.onclick = () => changeT(side, "feel.zones", [...SHAPES[b.dataset.shape]], true)));
    $$("[data-nudge]", card).forEach(b => (b.onclick = () => {
      const d = Number(b.dataset.nudge);
      changeT(side, "feel.zones", draft.triggers[side].feel.zones.map(v => (v ? Math.max(1, Math.min(8, v + d)) : v)), true);
    }));
    $("[data-feel]", card).onclick = () =>
      api("POST", "/api/test", { kind: "feel", trigger: side, feel: draft.triggers[side].feel, seconds: 4 });
    $("[data-add-reaction]", card).onclick = () => addReaction(side);
    $("[data-reset-throw]", card).onclick = () =>
      changeT(side, "output", { deadzone: 0, full_at: 100, curve: 0, max: 100 }, true);
    $("[data-goto]", card).onclick = () => select("output");
    grid.append(card);
  }
}

function renderPreset() {
  const p = draft;
  $("#title").textContent = p.name;
  $("#tb-sub").textContent = p.name;
  $("#subtitle").textContent = p.summary || "";
  const active = p.id === status.active;
  $("#use-preset").hidden = active;
  $("#in-use").hidden = !active;
  $("#act-reset").disabled = !(p.builtin && p.modified);
  $("#act-delete").disabled = !!p.builtin;
  document.documentElement.style.setProperty("--tint", p.lightbar);
  bind($("#view-preset .card.span"), "p", p, (path, v, structural) => {
    setPath(draft, path, v);
    if (path === "lightbar") {
      document.documentElement.style.setProperty("--tint", v);
      const dot = $(`.nav-row[data-view="preset:${draft.id}"] .swatch-dot`);
      if (dot) dot.style.setProperty("--c", v);
    }
    if (structural) renderPreset();
    queueSave();
  });
  const tele = p.telemetry !== "none";
  $("#source-hint").textContent = tele
    ? "Live data from Forza, F1, DiRT / GRID, BeamNG.drive or Live for Speed. Game-data reactions only work in this mode."
    : "Works in any game. Reactions follow your own presses, or the game's rumble.";
  $("#tele-row").hidden = !tele;
  $("#tele-setup").hidden = !tele;
  const ip = (status.ips && status.ips[0]) || "this computer's IP address";
  $$(".ip").forEach(el => (el.textContent = ip));
  for (const side of ["l2", "r2"]) renderTrigger(side);
  updateTelemetry();
}

function changeT(side, path, value, structural) {
  setPath(draft.triggers[side], path, value);
  if (structural) renderTrigger(side);
  else updateTriggerVisuals(side);
  queueSave();
}

function renderTrigger(side) {
  const card = $(`.trig[data-side="${side}"]`);
  const t = draft.triggers[side];
  const tab = tabs[side];
  $$("[data-tab]", card).forEach(b => {
    const on = b.dataset.tab === tab;
    b.setAttribute("aria-selected", String(on));
    b.tabIndex = on ? 0 : -1;
  });
  $$("[data-pane]", card).forEach(p => (p.hidden = p.dataset.pane !== tab));
  t.reactions_on ??= true;
  bind($(".card-head", card), "k", t, (path, v, s) => changeT(side, path, v, s));
  card.classList.toggle("reactions-off", !t.reactions_on);
  $("[data-off-note]", card).hidden = t.reactions_on;
  bind($('[data-pane="feel"]', card), "k", t, (path, v, s) => changeT(side, path, v, s));
  bind($('[data-pane="throw"]', card), "k", t, (path, v, s) => changeT(side, path, v, s));
  applyShow(card, "data-show", t);
  renderProfile($(".profile", card), t.feel.zones);
  renderReactions(side);
  $("[data-vpad-note]", card).hidden = !!status.settings.output.virtual;
  updateTriggerVisuals(side);
}

function updateTriggerVisuals(side) {
  const card = $(`.trig[data-side="${side}"]`);
  const t = draft.triggers[side];
  const sm = $('[data-viz="smooth"]', card);
  sm.style.setProperty("--a", t.feel.smooth.start + "%");
  sm.style.setProperty("--f", t.feel.smooth.force / 100);
  const ck = $('[data-viz="click"]', card);
  ck.style.setProperty("--a", t.feel.click.start * 10 + "%");
  ck.style.setProperty("--w", Math.max(0, t.feel.click.end - t.feel.click.start) * 10 + "%");
  ck.style.setProperty("--f", t.feel.click.force / 8);
  drawCurve(card, t.output);
}

// Zones editor: drag the bars or use arrow keys.
function buildProfile(el, side) {
  for (let i = 0; i < 10; i++) {
    const z = document.createElement("div");
    z.className = "zone"; z.tabIndex = i === 0 ? 0 : -1;
    z.setAttribute("role", "slider");
    z.setAttribute("aria-valuemin", "0"); z.setAttribute("aria-valuemax", "8");
    z.setAttribute("aria-label", `Resistance from ${i * 10}% to ${i * 10 + 10}% pull`);
    z.innerHTML = `<span class="num"></span><div class="bar"></div>`;
    el.append(z);
  }
  const finger = document.createElement("div");
  finger.className = "finger";
  el.append(finger);
  const setFrom = e => {
    const r = el.getBoundingClientRect(), zr = $(".zone", el).getBoundingClientRect();
    const i = Math.max(0, Math.min(9, Math.floor((e.clientX - r.left - 10) / ((r.width - 20) / 10))));
    const v = Math.max(0, Math.min(8, Math.round((1 - (e.clientY - zr.top) / zr.height) * 8)));
    const zones = [...draft.triggers[side].feel.zones];
    if (zones[i] === v) return;
    zones[i] = v;
    draft.triggers[side].feel.zones = zones;
    renderProfile(el, zones);
    queueSave();
  };
  el.addEventListener("pointerdown", e => { el.setPointerCapture(e.pointerId); setFrom(e); });
  el.addEventListener("pointermove", e => { if (el.hasPointerCapture(e.pointerId)) setFrom(e); });
  el.addEventListener("keydown", e => {
    const zs = $$(".zone", el), i = zs.indexOf(document.activeElement);
    if (i < 0) return;
    const zones = [...draft.triggers[side].feel.zones];
    if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      zones[i] = Math.max(0, Math.min(8, zones[i] + (e.key === "ArrowUp" ? 1 : -1)));
      draft.triggers[side].feel.zones = zones;
      renderProfile(el, zones);
      queueSave();
    } else if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
      const j = Math.max(0, Math.min(9, i + (e.key === "ArrowRight" ? 1 : -1)));
      zs.forEach((z, k) => (z.tabIndex = k === j ? 0 : -1));
      zs[j].focus();
    } else return;
    e.preventDefault();
  });
}
function renderProfile(el, zones) {
  $$(".zone", el).forEach((z, i) => {
    const v = zones[i] ?? 0;
    z.dataset.v = v; z.style.setProperty("--v", v);
    z.setAttribute("aria-valuenow", v);
    $(".num", z).textContent = v;
  });
}

// Throw: input -> output curve, mirrored from the engine's remap().
function remap(x, o) {
  const dz = o.deadzone / 100, full = Math.max(dz + 0.05, o.full_at / 100);
  if (x <= dz) return 0;
  return Math.pow(Math.min(1, (x - dz) / (full - dz)), Math.pow(2, o.curve / 50)) * o.max / 100;
}
function drawCurve(card, o) {
  const X = x => 20 + x * 190, Y = y => 200 - y * 190;
  let d = "";
  for (let i = 0; i <= 100; i++) d += (i ? "L" : "M") + X(i / 100).toFixed(1) + " " + Y(remap(i / 100, o)).toFixed(1);
  $(".c-line", card).setAttribute("d", d);
  $(".c-dz", card).setAttribute("width", (o.deadzone / 100) * 190);
  const full = Math.max(o.deadzone + 5, o.full_at);
  $(".c-sat", card).setAttribute("x", X(full / 100));
  $(".c-sat", card).setAttribute("width", 190 - (full / 100) * 190);
}

// Reactions
function reactionSummary(r) {
  const e = r.effect;
  const what = { kick: "Kick", buzz: "Buzz", wall: "Wall" }[e.type];
  const len = INSTANT.has(r.when) ? ` · ${e.duration_ms} ms` : "";
  return `${WHEN_TEXT[r.when](r)} → ${what} ${e.strength}${e.type === "buzz" ? ` · ${e.freq} Hz` : ""}${len}`;
}
function renderReactions(side) {
  const card = $(`.trig[data-side="${side}"]`);
  const list = $(".reactions", card);
  const rs = draft.triggers[side].reactions;
  list.replaceChildren(...rs.map((r, idx) => {
    const li = $("#tpl-reaction").content.firstElementChild.cloneNode(true);
    li.dataset.rid = r.id;
    const open = openReactions.has(side + r.id);
    const sum = $(".r-summary", li);
    $("span", sum).textContent = reactionSummary(r);
    sum.setAttribute("aria-expanded", String(open));
    $(".r-body", li).hidden = !open;
    sum.onclick = () => {
      openReactions.has(side + r.id) ? openReactions.delete(side + r.id) : openReactions.add(side + r.id);
      renderReactions(side);
    };
    const onChange = (path, v, structural) => {
      setPath(r, path, v);
      $("span", sum).textContent = reactionSummary(r);
      if (structural) renderReactions(side);
      queueSave();
    };
    bind(li, "r", r, onChange);
    r.rumble ??= { above: 30, scale: true };
    applyShow(li, "data-rshow", r);
    $("[data-rumble-note]", li).hidden = !!status.settings.output.virtual;
    const chips = $(".chips", li);
    chips.replaceChildren(...REACTION_BUTTONS.map(b => {
      const c = document.createElement("button");
      c.type = "button"; c.className = "chip"; c.textContent = BUTTON_NAMES[b];
      c.setAttribute("aria-pressed", String(r.buttons.includes(b)));
      c.onclick = () => {
        r.buttons = r.buttons.includes(b) ? r.buttons.filter(x => x !== b) : [...r.buttons, b];
        onChange("buttons", r.buttons, true);
      };
      return c;
    }));
    $$("[data-move]", li).forEach(b => {
      const to = idx + Number(b.dataset.move);
      b.disabled = to < 0 || to >= rs.length;
      b.onclick = () => { rs.splice(to, 0, rs.splice(idx, 1)[0]); renderReactions(side); queueSave(); };
    });
    $("[data-delete]", li).onclick = () => { rs.splice(idx, 1); renderReactions(side); queueSave(); };
    $("[data-feel-r]", li).onclick = () => api("POST", "/api/test", {
      kind: "reaction", trigger: side, effect: r.effect, hold: !INSTANT.has(r.when), seconds: 1.2 });
    return li;
  }));
}
function addReaction(side) {
  const id = "r" + Date.now().toString(36);
  draft.triggers[side].reactions.push({
    id, enabled: true, when: "buttons", buttons: [],
    effect: { type: "kick", strength: 7, duration_ms: 60, start: 0, freq: 45 },
    slam: { from: 30, to: 85, within: 200 }, held: { above: 90 },
  });
  openReactions.add(side + id);
  renderReactions(side);
  queueSave();
}

async function save() {
  saveTimer = null;
  if (!draft) return;
  try {
    status = await api("PUT", "/api/presets/" + draft.id, draft);
    const fresh = status.presets.find(p => p.id === draft.id);
    if (fresh) { draft.modified = fresh.modified; $("#act-reset").disabled = !(draft.builtin && draft.modified); }
  } catch (e) { banner("Couldn't save the preset: " + e.message); }
}

$("#use-preset").onclick = async () => {
  clearTimeout(saveTimer);
  await save();
  status = await api("POST", "/api/active", { id: draft.id });
  renderPreset();
  renderSidebar();
};

// More menu
const menu = $("#more-menu"), moreBtn = $("#more");
function closeMenu() { menu.hidden = true; moreBtn.setAttribute("aria-expanded", "false"); }
moreBtn.onclick = e => {
  e.stopPropagation();
  menu.hidden = !menu.hidden;
  moreBtn.setAttribute("aria-expanded", String(!menu.hidden));
  if (!menu.hidden) $("button:not(:disabled)", menu)?.focus();
};
document.addEventListener("click", closeMenu);
document.addEventListener("keydown", e => { if (e.key === "Escape") closeMenu(); });

async function duplicate(id) {
  const p = await api("POST", `/api/presets/${id}/duplicate`);
  status = await api("GET", "/api/status");
  select("preset:" + p.id);
}
$("#act-duplicate").onclick = () => duplicate(draft.id);
$("#new-preset").onclick = () => duplicate(draft ? draft.id : status.active);
$("#act-reset").onclick = async () => { status = await api("POST", `/api/presets/${draft.id}/reset`); select(view); };
$("#act-delete").onclick = () => { $("#confirm-title").textContent = `Delete “${draft.name}”?`; $("#confirm").showModal(); };
$("#confirm").addEventListener("close", async () => {
  if ($("#confirm").returnValue !== "ok") return;
  status = await api("DELETE", "/api/presets/" + draft.id);
  select("preset:" + status.active);
});
$("#act-rename").onclick = () => {
  $("#rename-name").value = draft.name;
  $("#rename-summary").value = draft.summary || "";
  $("#rename").showModal();
};
$("#rename").addEventListener("close", async () => {
  if ($("#rename").returnValue !== "ok") return;
  draft.name = $("#rename-name").value.trim() || draft.name;
  draft.summary = $("#rename-summary").value.trim();
  await save();
  renderPreset(); renderSidebar();
});
$("#enabled").onchange = e => api("POST", "/api/enabled", { enabled: e.target.checked });

// ------------------------------------------------------------------ lighting

async function saveSettings(patch) {
  try { status = await api("POST", "/api/settings", patch); }
  catch (e) { banner("Couldn't save: " + e.message); status = await api("GET", "/api/status"); }
}
let lightTimer = null;
function renderLighting() {
  const l = status.settings.lighting;
  bind($("#view-lighting"), "l", l, (path, v, structural) => {
    setPath(l, path, v);
    if (structural) renderLighting();
    clearTimeout(lightTimer);
    lightTimer = setTimeout(() => saveSettings({ lighting: l }), structural ? 0 : 120);
  });
  applyShow($("#view-lighting"), "data-lshow", l);
  const a = status.settings.app;
  bind($("#view-lighting"), "a", a, (path, v) => {
    setPath(a, path, v);
    applyGlass();
    clearTimeout(lightTimer);
    lightTimer = setTimeout(() => saveSettings({ app: a }), 200);
  });
  const sw = $("#light-swatches");
  sw.replaceChildren(...LIGHT_COLORS.map(c => {
    const b = document.createElement("button");
    b.type = "button"; b.setAttribute("role", "radio");
    b.style.setProperty("--c", c);
    b.setAttribute("aria-label", c);
    b.setAttribute("aria-checked", String(c.toLowerCase() === l.color.toLowerCase()));
    b.onclick = () => { l.color = c; saveSettings({ lighting: l }); renderLighting(); };
    return b;
  }));
  const pick = document.createElement("input");
  pick.type = "color"; pick.value = l.color; pick.setAttribute("aria-label", "Any color");
  const hex = document.createElement("input");
  hex.className = "hex"; hex.value = l.color; hex.setAttribute("aria-label", "Hex color");
  pick.oninput = () => {
    l.color = pick.value; hex.value = pick.value;
    clearTimeout(lightTimer); lightTimer = setTimeout(() => saveSettings({ lighting: l }), 80);
  };
  hex.onchange = () => {
    const v = hex.value.trim().replace(/^#?/, "#");
    if (/^#[0-9a-f]{6}$/i.test(v)) { l.color = v.toLowerCase(); saveSettings({ lighting: l }); renderLighting(); }
    else hex.value = l.color;
  };
  sw.append(pick, hex);
  const mode = { preset: "the preset's color", custom: "your color" }[l.mode];
  $("#light-caption").textContent = l.mode === "off"
    ? "The light bar stays off whenever the controller is connected."
    : `The light bar shows ${mode} whenever the controller is connected.`;
}
function updateLightPreview() {
  const c = live && live.light;
  document.documentElement.style.setProperty("--light", c && c !== "#000000" ? c : "transparent");
  const leds = live ? live.player_leds : 0;
  $$(".lp-leds rect").forEach((r, i) => r.classList.toggle("on", !!(leds & (1 << i))));
}

// ------------------------------------------------------------------ output

function renderOutput() {
  const o = status.settings.output;
  bind($("#view-output"), "o", o, (path, v) => { setPath(o, path, v); saveSettings({ output: o }).then(renderOutput); });
  $('[data-o="virtual"]').disabled = !o.virtual && !((live && live.virtual) || status.virtual).uinput;
  const rows = $("#map-rows");
  rows.replaceChildren(...["paddle_left", "paddle_right", "fn_left", "fn_right"].map(k => {
    const row = document.createElement("div");
    row.className = "row";
    row.innerHTML = `<div class="row-label"><strong></strong></div><select></select>`;
    $("strong", row).textContent = BUTTON_NAMES[k];
    const sel = $("select", row);
    sel.setAttribute("aria-label", BUTTON_NAMES[k] + " sends");
    for (const t of MAP_TARGETS) sel.add(new Option(t ? `Acts as ${BUTTON_NAMES[t]}` : "Nothing (reactions only)", t));
    sel.value = o.map[k] || "";
    sel.disabled = !o.virtual;
    sel.onchange = () => { o.map[k] = sel.value; saveSettings({ output: o }); };
    return row;
  }));
  updateOutputStatus();
}
function pill(id, text, kind) { const p = $(id); p.textContent = text; p.className = "pill " + (kind || ""); }
function updateOutputStatus() {
  if (!status || view !== "output") return;
  const o = status.settings.output, v = (live && live.virtual) || status.virtual;
  if (v.uinput) {
    pill("#o-access-pill", "Ready", "good");
    $("#o-access").textContent = "Controller Studio Pro can create virtual controllers.";
  } else {
    pill("#o-access-pill", "Needs setup", "warn");
    $("#o-access").innerHTML = `Run the installer again, or run once in a terminal: <code>sudo cp ${status.app_dir}/udev/*.rules /etc/udev/rules.d/ &amp;&amp; sudo udevadm control --reload &amp;&amp; sudo udevadm trigger</code>`;
  }
  const hidden = status.moonlight_hidden;
  if (o.virtual) {
    pill("#o-moon-pill", hidden ? "Set up" : "Not set up", hidden ? "good" : "warn");
    $("#o-moon").textContent = hidden
      ? "Moonlight ignores the real controller and uses the virtual one. Restart Moonlight after turning this on or off."
      : "Moonlight still sees the real controller.";
  } else {
    pill("#o-moon-pill", "Real controller", "");
    $("#o-moon").textContent = "Moonlight uses your controller directly.";
  }
  if (!o.virtual) { pill("#o-state-pill", "Off", ""); $("#o-state").textContent = "Games get your controller as-is."; }
  else if (v.active) { pill("#o-state-pill", "Running", "good"); $("#o-state").textContent = "“Controller Studio Pro Virtual Controller” is live with the active preset's throw settings."; }
  else if (v.error) { pill("#o-state-pill", "Error", "warn"); $("#o-state").textContent = v.error; }
  else { pill("#o-state-pill", "Waiting", ""); $("#o-state").textContent = "Starts when the controller connects."; }
}

// ------------------------------------------------------------------ live stream

function banner(msg) { const b = $("#banner"); b.hidden = !msg; b.textContent = msg || ""; }

function updateDevice() {
  const d = live && live.device, st = live && live.input;
  $("#device").classList.toggle("on", !!d);
  $("#device-name").textContent = d ? d.model : "No controller";
  $("#device-meta").textContent = d ? (d.bluetooth ? "Bluetooth" : "USB") : "Press the PS button to connect";
  if (st) {
    const batt = $("#battery"), charging = st.charging === "charging";
    batt.style.setProperty("--b", st.battery + "%");
    batt.classList.toggle("low", st.battery <= 15 && !charging);
    batt.classList.toggle("charging", charging);
    $("#batt-num").textContent = st.battery;
    batt.setAttribute("aria-label", `Battery ${st.battery}%${charging ? ", charging" : ""}`);
  }
  $("#enabled").checked = live ? live.enabled : true;
  if (!live) banner("Can't reach the Controller Studio Pro service. Start it with: systemctl --user start controller-studio-pro");
  else if (!d) banner("Controller not connected. Press the PS button to wake it.");
  else banner("");
}

function updateLive() {
  if (!draft) return;
  const st = live && live.input;
  for (const side of ["l2", "r2"]) {
    const card = $(`.trig[data-side="${side}"]`);
    if (!card) continue;
    const pull = st ? st[side] / 255 : 0;
    $('[data-t="pull"]', card).textContent = Math.round(pull * 100) + "%";
    const prof = $(".profile", card), f = $(".finger", prof);
    f.classList.toggle("on", pull > 0.01);
    f.style.setProperty("--p", pull * (prof.clientWidth - 22));
    $$(".tv-finger", card).forEach(el => el.style.setProperty("--p", pull));
    const out = remap(pull, draft.triggers[side].output);
    const dot = $(".c-dot", card);
    dot.setAttribute("cx", 20 + pull * 190);
    dot.setAttribute("cy", 200 - out * 190);
    const firing = live && live.firing && live.firing[side];
    $$(".reaction", card).forEach(li => li.classList.toggle("firing", li.dataset.rid === firing));
  }
}

function updateTelemetry() {
  if (!draft || draft.telemetry === "none") return;
  const t = live && live.telemetry, on = t && t.active;
  $("#tele-row").classList.toggle("live", !!on);
  const gear = t && (t.gear < 0 ? "R" : t.gear === 0 ? "N" : t.gear);
  $("#tele-state").textContent = on ? `Receiving ${t.source} data · gear ${gear} · ${t.speed} km/h` : "Waiting for game data…";
  $("#tele-help").textContent = on ? "Reactions are following the car."
    : "Start a supported game with its telemetry pointed at this computer (see Set up game data).";
}

let streamOK = false;
function connectStream() {
  const es = new EventSource("/api/stream");
  es.onmessage = e => {
    live = JSON.parse(e.data);
    if (!streamOK) { streamOK = true; refreshStatus(); }
    if (status && live.active !== status.active) refreshStatus();
    updateDevice();
    updateLightPreview();
    const tint = live.light && live.light !== "#000000" ? live.light : null;
    if (tint && !draft) document.documentElement.style.setProperty("--tint", tint);
    if (view === "tester") updateTester();
    else if (view === "output") updateOutputStatus();
    else { updateLive(); updateTelemetry(); }
  };
  es.onerror = () => { streamOK = false; live = null; updateDevice(); };
}
async function refreshStatus() {
  status = await api("GET", "/api/status");
  if (draft && !saveTimer) {
    const p = status.presets.find(p => p.id === draft.id);
    if (p) { draft = structuredClone(p); renderPreset(); }
  }
  renderSidebar();
}

// ------------------------------------------------------------------ tester

const trails = { l: [], r: [] }, rest = { l: null, r: null };
const fmt = (v, d) => (v >= 0 ? "+" : "−") + Math.abs(v).toFixed(d);

function updateTester() {
  const d = live.device, st = live.input;
  const pad = $("#pad");
  pad.classList.toggle("edge-off", !(d && /Edge/.test(d.model)));
  $("#i-model").textContent = d ? d.model : "Not connected";
  $("#i-conn").textContent = d ? (d.bluetooth ? "Bluetooth" : "USB") : "–";
  $("#i-addr").textContent = d && d.address ? d.address.toUpperCase() : "–";
  $("#i-rate").textContent = d ? `${live.rate} reports/s` : "–";
  $("#i-batt").textContent = st ? `${st.battery}% · ${st.charging}` : "–";
  if (!st) return;
  const down = new Set(st.buttons);
  $$("[data-btn]", pad).forEach(el => el.classList.toggle("down", down.has(el.dataset.btn)));
  $('[data-trig="l2"] .trig-fill', pad).setAttribute("width", (st.l2 / 255) * 96);
  $('[data-trig="r2"] .trig-fill', pad).setAttribute("width", (st.r2 / 255) * 96);
  $("#stick-l").setAttribute("cx", 232 + ((st.lx - 128) / 128) * 13);
  $("#stick-l").setAttribute("cy", 214 + ((st.ly - 128) / 128) * 13);
  $("#stick-r").setAttribute("cx", 408 + ((st.rx - 128) / 128) * 13);
  $("#stick-r").setAttribute("cy", 214 + ((st.ry - 128) / 128) * 13);
  $("#pad-touches").innerHTML = st.touches.map(t =>
    `<circle class="touch-dot" cx="${222 + (t.x / 1920) * 196}" cy="${70 + (t.y / 1080) * 104}" r="7"/>`).join("");
  const names = st.buttons.map(b => BUTTON_NAMES[b] || b);
  $("#pressed").textContent = names.length ? names.join(" + ") : "No buttons pressed";
  drawStick("l", $("#cv-l"), st.lx, st.ly, "#lval");
  drawStick("r", $("#cv-r"), st.rx, st.ry, "#rval");
  for (const k of ["l2", "r2"]) {
    $("#m-" + k).style.width = (st[k] / 255) * 100 + "%";
    $("#mo-" + k).style.left = `calc(${live.output[k]}% - 1.5px)`;
    $("#o-" + k).textContent = Math.round((st[k] / 255) * 100) + "%";
  }
  $("#touch-area").innerHTML = st.touches.map(t =>
    `<div class="pt" style="left:${(t.x / 1920) * 100}%;top:${(t.y / 1080) * 100}%">${t.id % 10}</div>`).join("");
  $("#touch-read").textContent = st.touches.length ? st.touches.map(t => `#${t.id}: ${t.x}, ${t.y}`).join("   ") : "No touches";
  const [ax, ay, az] = st.accel, [gx, gy, gz] = st.gyro;
  const pitch = Math.atan2(az, ay) * 180 / Math.PI, roll = Math.atan2(ax, ay) * 180 / Math.PI;
  $("#tilt").style.transform = `rotateX(${-pitch}deg) rotateZ(${roll}deg)`;
  $("#tilt-read").textContent = `pitch ${fmt(pitch, 0)}°  roll ${fmt(roll, 0)}°`;
  $("#gyro-read").textContent = [gx, gy, gz].map(v => fmt(v / 16.4, 0).padStart(4)).join("  ") + " °/s";
  $("#accel-read").textContent = [ax, ay, az].map(v => fmt(v / 8192, 2)).join("  ") + " g";
}
function drawStick(k, cv, x, y, out) {
  const nx = (x - 128) / 127.5, ny = (y - 128) / 127.5, mag = Math.hypot(nx, ny), tr = trails[k];
  if (mag > 0.15) { tr.push([nx, ny]); if (tr.length > 1500) tr.shift(); } else rest[k] = mag;
  const css = getComputedStyle(document.documentElement);
  const c = cv.getContext("2d"), w = cv.width, R = w / 2 - 14, cx = w / 2, cy = w / 2;
  c.clearRect(0, 0, w, w);
  c.lineWidth = 2; c.strokeStyle = css.getPropertyValue("--fill-strong"); c.fillStyle = css.getPropertyValue("--fill");
  c.beginPath(); c.arc(cx, cy, R, 0, Math.PI * 2); c.fill(); c.stroke();
  c.lineWidth = 1;
  c.beginPath(); c.moveTo(cx - R, cy); c.lineTo(cx + R, cy); c.moveTo(cx, cy - R); c.lineTo(cx, cy + R); c.stroke();
  c.beginPath(); c.arc(cx, cy, R * 0.1, 0, Math.PI * 2); c.stroke();
  c.fillStyle = css.getPropertyValue("--accent"); c.globalAlpha = 0.35;
  for (const [tx, ty] of tr) c.fillRect(cx + tx * R - 1.5, cy + ty * R - 1.5, 3, 3);
  c.globalAlpha = 1; c.shadowColor = c.fillStyle; c.shadowBlur = 14;
  c.beginPath(); c.arc(cx + nx * R, cy + ny * R, 12, 0, Math.PI * 2); c.fill();
  c.shadowBlur = 0;
  const drift = rest[k] == null ? "" : `  ·  drift ${(rest[k] * 100).toFixed(1)}%`;
  $(out).textContent = `${fmt(nx, 2)}, ${fmt(-ny, 2)}${drift}`;
}
$("#clear-trails").onclick = () => { trails.l = []; trails.r = []; };

function segValue(id) { return $(`#${id} [aria-checked="true"]`).dataset.value; }
$$("#lab-side, #lab-kind").forEach(seg => {
  $$("button", seg).forEach(b => (b.onclick = () => {
    $$("button", seg).forEach(x => { x.setAttribute("aria-checked", String(x === b)); x.tabIndex = x === b ? 0 : -1; });
    $("#lab-freq-row").hidden = segValue("lab-kind") !== "vibrate";
  }));
  $$("button", seg).forEach(x => (x.tabIndex = x.getAttribute("aria-checked") === "true" ? 0 : -1));
  seg.onkeydown = segKeys;
});
$("#lab-freq-row").hidden = true;
$$("#view-tester input[type=range]").forEach(inp => (inp.oninput = () => showOutput(inp)));
$("#lab-go").onclick = () => {
  const s = Number($("#lab-strength").value), step = Math.max(1, Math.ceil(s / 12.5));
  const feel = {
    type: segValue("lab-kind"),
    zones: Array(10).fill(step), smooth: { start: 0, force: s },
    click: { start: 4, end: 6, force: step }, vibrate: { start: 0, amplitude: step, freq: Number($("#lab-freq").value) },
  };
  api("POST", "/api/test", { kind: "feel", trigger: segValue("lab-side"), feel, seconds: 10 });
};
$("#lab-stop").onclick = () => api("POST", "/api/test", { kind: "stop" });
$("#rumble").onclick = () => api("POST", "/api/rumble", { strong: $("#rum-strong").value / 100, weak: $("#rum-weak").value / 100, ms: 1000 });

// ------------------------------------------------------------------ boot

// Boot: a light traces the controller outline, the screen goes black, then the app.
function playSplash() {
  const splash = $("#splash"), app = $("#app");
  const reduce = document.documentElement.classList.contains("reduce-motion")
    || matchMedia("(prefers-reduced-motion: reduce)").matches;
  let done = false;
  const finish = () => {
    if (done) return;
    done = true;
    splash.classList.add("dark");
    setTimeout(() => {
      splash.classList.add("out");
      app.classList.add("enter");
      setTimeout(() => { splash.remove(); app.classList.remove("enter"); }, 700);
    }, reduce ? 0 : 180);
  };
  if (reduce) return setTimeout(finish, 50);
  const comet = $(".boot-comet"), trail = $(".boot-trail");
  const len = comet.getTotalLength();
  const head = 90, run = 780;
  comet.style.strokeDasharray = `${head} ${len}`;
  trail.style.strokeDasharray = `${len} ${len}`;
  const glow = $(".boot-glow");
  glow.style.strokeDasharray = `${head} ${len}`;
  for (const el of [comet, glow])
    el.animate([{ strokeDashoffset: head }, { strokeDashoffset: head - len }], { duration: run, easing: "cubic-bezier(.45,0,.25,1)", fill: "forwards" });
  trail.animate([{ strokeDashoffset: len }, { strokeDashoffset: 0 }], { duration: run, easing: "cubic-bezier(.45,0,.25,1)", fill: "forwards" });
  setTimeout(finish, run + 60);
  splash.addEventListener("click", finish);
  document.addEventListener("keydown", finish, { once: true });
}

function applyGlass() {
  const g = status && status.settings.app ? status.settings.app.glass : 60;
  document.documentElement.style.setProperty("--frost", g / 100);
}

// Frameless window: the native host draws nothing, so the page provides drag, resize and buttons.
const host = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.win;
if (host) {
  document.documentElement.classList.add("framed");
  const NO_DRAG = "button, input, select, a, label, [role=slider], .seg, .nav-row, .profile, canvas, .card";
  document.addEventListener("mousedown", e => {
    if (e.button !== 0) return;
    const edge = e.target.closest("[data-edge]");
    if (edge) { host.postMessage("resize:" + edge.dataset.edge); e.preventDefault(); return; }
    if (e.target.closest("[data-drag]") && !e.target.closest(NO_DRAG)) { host.postMessage("drag"); e.preventDefault(); }
  });
  document.addEventListener("dblclick", e => {
    if (e.target.closest(".titlebar") && !e.target.closest(NO_DRAG)) host.postMessage("maximize");
  });
  $$("[data-win]").forEach(b => (b.onclick = () => host.postMessage(b.dataset.win)));
}

(async function boot() {
  if (params.get("nosplash") === "1") $("#splash").remove(); else playSplash();
  for (;;) {
    try { status = await api("GET", "/api/status"); applyGlass(); break; }
    catch { banner("Can't reach the Controller Studio Pro service. Start it with: systemctl --user start controller-studio-pro"); await new Promise(r => setTimeout(r, 2000)); }
  }
  let saved = decodeURIComponent(location.hash.slice(1)) || null;
  try { saved ??= localStorage.getItem("view"); } catch {}
  const valid = v => v && (PAGES[v] || status.presets.some(p => "preset:" + p.id === v));
  select(valid(saved) ? saved : "preset:" + status.active);
  connectStream();
})();
