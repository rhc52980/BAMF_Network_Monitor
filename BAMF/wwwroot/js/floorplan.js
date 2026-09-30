// ---- Floor plan ----
// A plan image per floor, and a pin for each device at its spot on one. Pins
// take the device's state: green online, amber online and unknown, and red
// with a swelling ring when offline, so the camera that dropped is easy to
// find. Placing is click-to-place (pick a device, click the plan), which
// works the same with a mouse and a finger; pins drag to move.
let floorData = null, floorLoadedAt = 0, floorEditing = false, floorArmed = null, floorSel = null, floorArmedGroup = null;
let floorId = null;
try { floorId = Number(localStorage.getItem("bamf-floor")) || null; } catch { }
function loadFloors(force) {
  if (!force && floorData && Date.now() - floorLoadedAt < 60e3) return Promise.resolve(floorData);
  return fetch("/api/floors").then(r => r.ok ? r.json() : null).then(d => { if (d) { floorData = d; floorLoadedAt = Date.now(); } return floorData; }).catch(() => floorData);
}
function floorState(h) { return !h.online ? "off" : h.known ? "on" : "warn"; }
// ---- Floor plans drawn in BAMF ----
// A plan is walls, doors, windows and labels, in the same coordinate space as
// an uploaded image, so a device's spot means the same thing either way.
const PLAN_W = 1200, PLAN_H = 800, PLAN_STEP = 40;
const planCache = new Map();
const newPlan = () => ({ v: 1, width: PLAN_W, height: PLAN_H, unit: "ft", step: PLAN_STEP, perStep: 1, items: [] });
// How long a wall is, in whatever the plan is measured in.
const planLen = (plan, a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]) / plan.step * plan.perStep;
const planShow = (plan, v) => (v < 10 ? v.toFixed(1) : Math.round(v)) + (plan.unit === "m" ? " m" : " ft");
// Pixels for a real-world length, used to size doors and windows.
const planPx = (plan, v) => v / plan.perStep * plan.step;

function planSvg(plan, { grid = false, measure = false } = {}) {
  const e = [`<rect x="0" y="0" width="${plan.width}" height="${plan.height}" fill="#f6f3ec"/>`];
  if (grid) {
    for (let x = 0; x <= plan.width; x += plan.step) e.push(`<line class="fp-grid${x % (plan.step * 5) ? "" : " big"}" x1="${x}" y1="0" x2="${x}" y2="${plan.height}"/>`);
    for (let y = 0; y <= plan.height; y += plan.step) e.push(`<line class="fp-grid${y % (plan.step * 5) ? "" : " big"}" x1="0" y1="${y}" x2="${plan.width}" y2="${y}"/>`);
  }
  for (const it of plan.items) if (it.k === "wall") e.push(`<line class="fp-wall" x1="${it.a[0]}" y1="${it.a[1]}" x2="${it.b[0]}" y2="${it.b[1]}"/>`);
  // Doors and windows cut the wall they sit on, then draw their own mark.
  for (const it of plan.items) {
    if (it.k !== "door" && it.k !== "window") continue;
    e.push(`<line class="fp-open" x1="${it.a[0]}" y1="${it.a[1]}" x2="${it.b[0]}" y2="${it.b[1]}"/>`);
    if (it.k === "window") { e.push(`<line class="fp-win" x1="${it.a[0]}" y1="${it.a[1]}" x2="${it.b[0]}" y2="${it.b[1]}"/>`); continue; }
    // A door: the leaf, and the quarter circle it swings through.
    const dx = it.b[0] - it.a[0], dy = it.b[1] - it.a[1], w = Math.hypot(dx, dy) || 1;
    const nx = -dy / w, ny = dx / w;
    e.push(`<path class="fp-door" d="M${it.a[0]} ${it.a[1]}L${(it.a[0] + nx * w).toFixed(1)} ${(it.a[1] + ny * w).toFixed(1)}"/>`);
    e.push(`<path class="fp-door" d="M${(it.a[0] + nx * w).toFixed(1)} ${(it.a[1] + ny * w).toFixed(1)}A${w.toFixed(1)} ${w.toFixed(1)} 0 0 ${dx * ny - dy * nx > 0 ? 1 : 0} ${it.b[0]} ${it.b[1]}"/>`);
  }
  for (const it of plan.items) if (it.k === "label") e.push(`<text class="fp-label" x="${it.p[0]}" y="${it.p[1]}" text-anchor="middle">${esc(it.t)}</text>`);
  if (measure) {
    for (const it of plan.items) {
      if (it.k !== "wall") continue;
      const mx = (it.a[0] + it.b[0]) / 2, my = (it.a[1] + it.b[1]) / 2;
      const dx = it.b[0] - it.a[0], dy = it.b[1] - it.a[1];
      const w = Math.hypot(dx, dy) || 1, nx = -dy / w * 13, ny = dx / w * 13;
      e.push(`<text class="fp-meas" x="${(mx + nx).toFixed(0)}" y="${(my + ny).toFixed(0)}" text-anchor="middle">${esc(planShow(plan, planLen(plan, it.a, it.b)))}</text>`);
    }
  }
  return e.join("");
}

function loadPlan(floor) {
  const key = floor.id + ":" + floor.updated;
  if (planCache.has(key)) return Promise.resolve(planCache.get(key));
  return fetch(`/api/floors/${floor.id}/plan`).then(r => r.ok ? r.json() : null).then(d => {
    if (!d) return null;
    // Server-side names are the long ones; the page works in short ones.
    const plan = { v: 1, width: d.Width ?? d.width, height: d.Height ?? d.height, unit: d.Unit ?? d.unit, step: d.Step ?? d.step, perStep: d.PerStep ?? d.perStep,
      items: (d.Items ?? d.items ?? []).map(i => ({ k: i.K ?? i.k, a: i.A ?? i.a, b: i.B ?? i.b, p: i.P ?? i.p, t: i.T ?? i.t })) };
    planCache.set(key, plan);
    return plan;
  }).catch(() => null);
}

async function renderFloor() {
  const d = await loadFloors();
  if (!d || view !== "floor") return;
  const floors = d.floors;
  if (!floors.some(f => f.id === floorId)) floorId = floors.length ? floors[0].id : null;
  const byId = new Map(hosts.map(h => [h.id, h]));
  // Floor tabs, each with how many of its devices are offline.
  $("floorTabs").innerHTML = "";
  for (const f of floors) {
    const mine = d.places.filter(pl => pl.floorId === f.id).map(pl => byId.get(pl.hostId)).filter(Boolean);
    const off = mine.filter(h => !h.online && !h.ignored).length;
    const b = document.createElement("button");
    b.type = "button";
    b.className = "floor-tab" + (f.id === floorId ? " active" : "");
    b.innerHTML = `${esc(f.name)}<span class="n${off ? " off" : ""}" title="${off ? off + " offline of " : ""}${mine.length} device${mine.length === 1 ? "" : "s"} on this floor">${off ? off + " off" : mine.length}</span>`;
    b.onclick = () => { floorId = f.id; floorSel = null; try { localStorage.setItem("bamf-floor", f.id); } catch { } renderFloor(); };
    $("floorTabs").appendChild(b);
  }
  const floor = floors.find(f => f.id === floorId);
  $("floorEmpty").hidden = !!floor;
  $("floorMain").hidden = !floor;
  $("floorEdit").hidden = !floor;
  $("floorEditBar").hidden = !floor || !floorEditing;
  $("floorSide").hidden = !floor || !floorEditing;
  $("floorEdit").setAttribute("aria-pressed", String(floorEditing));
  $("floorEdit").textContent = floorEditing ? "Done" : "Place devices";
  if (!floor) return;
  const img = $("floorImg");
  const drawn = floor.kind === "plan";
  $("floorReplace").hidden = drawn;
  $("floorRedraw").hidden = !drawn;
  img.hidden = drawn;
  $("floorSvg").hidden = !drawn;
  if (drawn) {
    const plan = await loadPlan(floor);
    if (!plan) { $("floorSvg").innerHTML = `<div class="empty">That plan couldn't be loaded.</div>`; return; }
    $("floorSvg").innerHTML = `<svg viewBox="0 0 ${plan.width} ${plan.height}" width="${plan.width}" height="${plan.height}" role="img" aria-label="Floor plan: ${esc(floor.name)}">${planSvg(plan)}</svg>`;
  } else {
    const src = `/api/floors/${floor.id}/image?v=${encodeURIComponent(floor.updated)}`;
    if (img.getAttribute("src") !== src) { img.src = src; img.alt = "Floor plan: " + floor.name; }
  }
  const stage = $("floorStage");
  stage.classList.toggle("editing", floorEditing);
  stage.classList.toggle("arming", floorEditing && (floorArmed !== null || floorArmedGroup !== null));
  // Pins.
  const offlineOnly = $("floorShow").value === "offline";
  const pins = $("floorPins");
  pins.innerHTML = "";
  for (const pl of d.places.filter(pl => pl.floorId === floor.id)) {
    const h = byId.get(pl.hostId);
    if (!h || h.forgotten) continue;
    if (offlineOnly && h.online) continue;
    const pin = document.createElement("div");
    pin.className = `fp-pin ${floorState(h)}` + (floorSel === h.id ? " sel" : "");
    pin.style.left = (pl.x * 100) + "%";
    pin.style.top = (pl.y * 100) + "%";
    pin.dataset.id = h.id;
    pin.title = `${nameOrIp(h)} · ${h.ip}\n${h.online ? (h.known ? "Online" : "Online, unknown") : "Offline, last seen " + fmtAgo(h.lastSeen)}` + (floorEditing ? "" : "\nClick to show it in Devices");
    pin.innerHTML = `<span class="ico">${typeIconSvg(deviceKind(h))}</span><span class="lbl">${esc(nameOrIp(h))}</span>` +
      (floorEditing ? `<button type="button" class="rm" title="Take it off this floor">✕</button>` : "");
    pins.appendChild(pin);
  }
  if (floorEditing) renderFloorList(d, floor);
}
function renderFloorList(d, floor) {
  const q = $("floorFind").value.trim().toLowerCase();
  const placed = new Map(d.places.map(pl => [pl.hostId, pl.floorId]));
  const names = new Map(d.floors.map(f => [f.id, f.name]));
  const pool = hosts.filter(h => !h.remote && !h.forgotten && !h.ignored && (!q || matchesQuery(h, q)))
    .sort((a, b) => nameOrIp(a).localeCompare(nameOrIp(b)));
  const item = h => {
    const where = placed.has(h.id) ? (placed.get(h.id) === floor.id ? "here" : "on " + names.get(placed.get(h.id))) : "";
    return `<button type="button" class="fl-item${floorArmed === h.id ? " armed" : ""}" data-id="${h.id}"><span class="d${h.online ? " on" : ""}"></span>${esc(nameOrIp(h))}<small>${esc(where || h.ip)}</small></button>`;
  };
  const off = pool.filter(h => !placed.has(h.id)), elsewhere = pool.filter(h => placed.has(h.id));
  // Devices you've recorded on a switch or an SSID are grouped by it: things
  // on one access point are usually in the same part of the house, so they can
  // go down together instead of one at a time.
  const groups = new Map();
  for (const h of off) {
    const sw = h.switchId ? switches.find(x => x.id === h.switchId) : null;
    const key = sw ? "s" + sw.id : "";
    if (!groups.has(key)) groups.set(key, { name: sw ? sw.name : "Not recorded anywhere", list: [] });
    groups.get(key).list.push(h);
  }
  const ordered = [...groups.entries()].sort((a, b) => (a[0] === "" ? 1 : 0) - (b[0] === "" ? 1 : 0) || b[1].list.length - a[1].list.length);
  let html = "";
  if (off.length) {
    html += `<div class="fl-sec">Not on a floor yet (${off.length})</div>`;
    for (const [key, g] of ordered) {
      const armed = floorArmedGroup === key;
      html += key
        ? `<div class="fl-sec grp${armed ? " armed" : ""}">${esc(g.name)} <button type="button" data-group="${esc(key)}" title="Put all ${g.list.length} on this floor at once">${armed ? "pick a spot\u2026" : "place all " + g.list.length}</button></div>`
        : `<div class="fl-sec">${esc(g.name)}</div>`;
      html += g.list.map(item).join("");
    }
  }
  html += elsewhere.length ? `<div class="fl-sec">Already placed</div>` + elsewhere.map(item).join("") : "";
  $("floorList").innerHTML = html || `<div class="fl-sec">No devices match</div>`;
}
// A tidy block of spots around where you clicked, for a whole group at once.
function floorCluster(n, x, y) {
  const cols = Math.ceil(Math.sqrt(n)), gap = .055;
  const out = [];
  for (let i = 0; i < n; i++) {
    const c = i % cols, r = Math.floor(i / cols);
    out.push([
      Math.min(.98, Math.max(.02, x + (c - (cols - 1) / 2) * gap)),
      Math.min(.98, Math.max(.02, y + (r - (Math.ceil(n / cols) - 1) / 2) * gap)),
    ]);
  }
  return out;
}
async function placeGroup(key, x, y) {
  const placed = new Set(floorData.places.map(pl => pl.hostId));
  const list = hosts.filter(h => !h.remote && !h.forgotten && !h.ignored && !placed.has(h.id)
    && (key === "" ? !h.switchId : "s" + h.switchId === key));
  if (!list.length) { toast("Nothing left to place there"); return; }
  const spots = floorCluster(list.length, x, y);
  for (let i = 0; i < list.length; i++) {
    const r = await fetch(`/api/floors/${floorId}/places`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ hostId: list[i].id, x: spots[i][0], y: spots[i][1] }) });
    if (!r.ok) { toast("Couldn't place them all"); break; }
    floorData.places.push({ hostId: list[i].id, floorId, x: spots[i][0], y: spots[i][1] });
  }
  renderFloor();
  toast(`Placed ${list.length} device${list.length === 1 ? "" : "s"}: drag any of them where it belongs`);
}
async function placeOnFloor(hostId, x, y) {
  const r = await fetch(`/api/floors/${floorId}/places`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ hostId, x, y }) });
  if (!r.ok) { toast("Couldn't place that device"); return; }
  const d = floorData, pl = d.places.find(p => p.hostId === hostId);
  if (pl) Object.assign(pl, { floorId, x, y }); else d.places.push({ hostId, floorId, x, y });
  renderFloor();
}
async function removeFromFloor(hostId) {
  const r = await fetch(`/api/floors/places/${hostId}`, { method: "DELETE" });
  if (!r.ok) { toast("Couldn't take it off"); return; }
  floorData.places = floorData.places.filter(p => p.hostId !== hostId);
  floorSel = null;
  renderFloor();
}
// Where a pointer is on the plan, as shares of its width and height. The plan
// is an uploaded image or a drawing; measure against whichever is showing, not
// the hidden one, or every spot lands at the bottom of the plan.
function floorPlanEl() {
  const svg = document.querySelector("#floorSvg svg");
  return svg && !document.getElementById("floorSvg").hidden ? svg : document.getElementById("floorImg");
}
function floorPoint(e) {
  const r = floorPlanEl().getBoundingClientRect();
  if (!r.width || !r.height) return [.5, .5];
  return [Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), Math.min(1, Math.max(0, (e.clientY - r.top) / r.height))];
}
{
  const stage = document.getElementById("floorStage");
  let drag = null;
  stage.addEventListener("pointerdown", e => {
    const pin = e.target.closest(".fp-pin");
    if (e.target.closest(".rm")) return;
    if (!floorEditing) return;
    // With a device picked from the list, a click anywhere places it, on top of another pin or not.
    if (pin && floorArmed === null) {
      const id = Number(pin.dataset.id);
      floorSel = id;
      document.querySelectorAll(".fp-pin.sel").forEach(p => p.classList.remove("sel"));
      pin.classList.add("sel");
      drag = { id, pin, moved: false, sx: e.clientX, sy: e.clientY };
      try { stage.setPointerCapture(e.pointerId); } catch { }
      e.preventDefault();
    }
  });
  stage.addEventListener("pointermove", e => {
    if (!drag) return;
    if (!drag.moved && Math.hypot(e.clientX - drag.sx, e.clientY - drag.sy) < 4) return;
    drag.moved = true;
    stage.classList.add("dragging");
    const [x, y] = floorPoint(e);
    drag.pin.style.left = (x * 100) + "%";
    drag.pin.style.top = (y * 100) + "%";
  });
  stage.addEventListener("pointerup", e => {
    stage.classList.remove("dragging");
    if (drag) {
      const d = drag; drag = null;
      if (d.moved) { const [x, y] = floorPoint(e); placeOnFloor(d.id, x, y); }
      return;
    }
    // Click-to-place: a device picked in the list goes where the plan was clicked.
    if (floorEditing && floorArmedGroup !== null) {
      const [x, y] = floorPoint(e);
      const key = floorArmedGroup;
      floorArmedGroup = null;
      placeGroup(key, x, y);
      return;
    }
    if (floorEditing && floorArmed !== null) {
      const [x, y] = floorPoint(e);
      const id = floorArmed;
      floorArmed = null;
      floorSel = id;
      placeOnFloor(id, x, y);
    }
  });
  stage.addEventListener("click", e => {
    const rm = e.target.closest(".rm");
    if (rm) { removeFromFloor(Number(rm.closest(".fp-pin").dataset.id)); return; }
    const pin = e.target.closest(".fp-pin");
    if (pin && !floorEditing) jumpToHost(Number(pin.dataset.id));
  });
}
document.getElementById("floorList").addEventListener("click", e => {
  const it = e.target.closest(".fl-item");
  if (!it) return;
  const id = Number(it.dataset.id);
  floorArmed = floorArmed === id ? null : id;
  renderFloor();
  if (floorArmed !== null) toast(`Now click on the plan where ${nameOrIp(hosts.find(h => h.id === id))} goes`);
});
document.getElementById("floorFind").addEventListener("input", () => { if (floorData) renderFloorList(floorData, floorData.floors.find(f => f.id === floorId)); });
document.getElementById("floorShow").onchange = () => renderFloor();
document.getElementById("floorEdit").onclick = () => { floorEditing = !floorEditing; floorArmed = null; floorArmedGroup = null; floorSel = null; renderFloor(); };
// "Place all" arms the group; the next click on the plan drops them in a block.
document.getElementById("floorList").addEventListener("click", e => {
  const b = e.target.closest("button[data-group]");
  if (!b) return;
  e.stopPropagation();
  const key = b.dataset.group;
  floorArmed = null;
  floorArmedGroup = floorArmedGroup === key ? null : key;
  if (floorArmedGroup !== null) toast("Now click where they go: they'll land in a block you can tidy up");
  renderFloor();
});
// Adding a floor, or replacing a floor's image: the file picker, then its
// size read in the browser, then the bytes as the request body.
let floorPick = null;
function pickFloorImage(then) { floorPick = then; const f = document.getElementById("floorFile"); f.value = ""; f.click(); }
document.getElementById("floorFile").onchange = async e => {
  const file = e.target.files[0];
  if (!file || !floorPick) return;
  if (file.size > 12 * 1024 * 1024) { toast("That image is over 12 MB"); return; }
  const url = URL.createObjectURL(file);
  const size = await new Promise(res => { const im = new Image(); im.onload = () => res([im.naturalWidth, im.naturalHeight]); im.onerror = () => res(null); im.src = url; });
  URL.revokeObjectURL(url);
  if (!size) { toast("That doesn't look like an image BAMF can use: PNG, JPEG or WebP"); return; }
  const go = floorPick; floorPick = null;
  await go(file, size[0], size[1]);
};
document.getElementById("floorAdd").onclick = () => {
  const name = prompt("Name this floor (e.g. Ground floor, Upstairs, Garage):", floorData && floorData.floors.length ? "" : "Ground floor");
  if (name === null) return;
  pickFloorImage(async (file, w, h) => {
    const r = await fetch(`/api/floors?name=${encodeURIComponent(name)}&width=${w}&height=${h}`, { method: "POST", headers: { "Content-Type": file.type || "application/octet-stream" }, body: file });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't add that floor"); return; }
    floorId = d.id;
    try { localStorage.setItem("bamf-floor", d.id); } catch { }
    floorEditing = true;
    await loadFloors(true);
    renderFloor();
    toast("Floor added: pick a device on the right, then click where it goes");
  });
};
// ---- The drawing editor ----
// Walls are drawn by clicking corner to corner; they straighten to the nearest
// right angle or diagonal, snap to the ends of walls already there, and settle
// on the grid otherwise. Every wall says how long it is, and the last one can
// be given an exact length by typing it.
let planDoc = null, planTool = "wall", planFrom = null, planAt = null, planDrag = null, planTarget = null, planLast = null;
// How big the drawing area is, and how big it looks on screen. A plan can be
// a room or a few hundred feet of yard, so the area grows in steps and the
// zoom keeps it on screen while you draw.
let planZoom = 1, planStartSize = null;
const PLAN_SIZES = [[1200, 800], [1800, 1200], [2400, 1600], [3600, 2400], [4800, 3200], [6000, 4000]];
const planSizeIndex = () => {
  let best = 0;
  PLAN_SIZES.forEach(([w], i) => { if (planDoc && Math.abs(w - planDoc.width) < Math.abs(PLAN_SIZES[best][0] - planDoc.width)) best = i; });
  return best;
};
function planSetZoom(z) {
  planZoom = Math.max(.15, Math.min(2, z));
  const svg = planEl();
  svg.style.width = (planDoc.width * planZoom).toFixed(0) + "px";
  svg.style.height = (planDoc.height * planZoom).toFixed(0) + "px";
  // Zoomed out, everything drawn in plan units would shrink to nothing, so
  // line weights and text grow to match. Zoomed in, they stay as they are.
  planEl().style.setProperty("--fpk", (planZoom < 1 ? Math.min(4, 1 / planZoom) : 1).toFixed(2));
  $("planZoom").textContent = Math.round(planZoom * 100) + "%";
  planLenBox();
}
function planFit() {
  const box = $("planCanvas").getBoundingClientRect();
  planSetZoom(Math.min(1, (box.width - 40) / planDoc.width, (box.height - 40) / planDoc.height));
}
// What the plan covers in the real world, which is the number that matters.
function planSizeLabel() {
  const w = planDoc.width / planDoc.step * planDoc.perStep, h = planDoc.height / planDoc.step * planDoc.perStep;
  $("planSize").textContent = `${Math.round(w)} × ${Math.round(h)} ${planDoc.unit}`;
}
function planResize(step) {
  const i = Math.max(0, Math.min(PLAN_SIZES.length - 1, planSizeIndex() + step));
  const [w, h] = PLAN_SIZES[i];
  if (w === planDoc.width && h === planDoc.height) { toast(step > 0 ? "That's the biggest area BAMF draws" : "That's the smallest area"); return; }
  planDoc.width = w;
  planDoc.height = h;
  planEl().setAttribute("viewBox", `0 0 ${w} ${h}`);
  planFit();
  planSizeLabel();
  planDraw();
}
const planEl = () => document.getElementById("planSvgEd");
const PLAN_HINTS = {
  wall: "Click each corner. Walls straighten to right angles and diagonals and snap to walls already drawn. Type over the length to set it exactly. Esc or double-click finishes a run.",
  room: "Drag out a room and it becomes four walls.",
  door: "Click a wall where the door goes.",
  window: "Click a wall where the window goes.",
  label: "Click where a room's name goes.",
  erase: "Click a wall, door, window or name to remove it.",
};

function planPoint(e) {
  const svg = planEl(), r = svg.getBoundingClientRect();
  return [(e.clientX - r.left) / r.width * planDoc.width, (e.clientY - r.top) / r.height * planDoc.height];
}
// Every end of every wall, so a new one can land exactly on an old one.
function planEnds() {
  const out = [];
  for (const it of planDoc.items) if (it.k === "wall") { out.push(it.a); out.push(it.b); }
  return out;
}
function planSnap(pt, from) {
  const near = planEnds().find(q => Math.hypot(q[0] - pt[0], q[1] - pt[1]) < 14);
  if (near) return [near[0], near[1]];
  let [x, y] = pt;
  if (from) {
    // Straighten: right angles first, then the diagonals.
    const dx = x - from[0], dy = y - from[1], len = Math.hypot(dx, dy);
    if (len > 4) {
      const a = Math.atan2(dy, dx), step = Math.PI / 4;
      const snapped = Math.round(a / step) * step;
      if (Math.abs(((a - snapped + Math.PI * 3) % (Math.PI * 2)) - Math.PI) > Math.PI - 0.13) {
        x = from[0] + Math.cos(snapped) * len;
        y = from[1] + Math.sin(snapped) * len;
      }
    }
  }
  const g = planDoc.step / 2;
  const gx = Math.round(x / g) * g, gy = Math.round(y / g) * g;
  if (Math.hypot(gx - x, gy - y) < 9) { x = gx; y = gy; }
  return [Math.max(0, Math.min(planDoc.width, x)), Math.max(0, Math.min(planDoc.height, y))];
}
// The wall nearest a point, and where on it that point falls.
function planNearestWall(pt) {
  let best = null;
  for (const it of planDoc.items) {
    if (it.k !== "wall") continue;
    const dx = it.b[0] - it.a[0], dy = it.b[1] - it.a[1], L2 = dx * dx + dy * dy || 1;
    const t = Math.max(0, Math.min(1, ((pt[0] - it.a[0]) * dx + (pt[1] - it.a[1]) * dy) / L2));
    const px = it.a[0] + dx * t, py = it.a[1] + dy * t, d = Math.hypot(px - pt[0], py - pt[1]);
    if (!best || d < best.d) best = { it, t, at: [px, py], d, len: Math.sqrt(L2) };
  }
  return best;
}
function planOpening(pt, kind) {
  const w = planNearestWall(pt);
  if (!w || w.d > 26) { toast("Click on a wall to put a " + kind + " in it"); return; }
  const want = planPx(planDoc, kind === "door" ? (planDoc.unit === "m" ? .9 : 3) : (planDoc.unit === "m" ? 1.2 : 4));
  const half = Math.min(want, w.len * .8) / 2;
  const ux = (w.it.b[0] - w.it.a[0]) / w.len, uy = (w.it.b[1] - w.it.a[1]) / w.len;
  const c = Math.max(half, Math.min(w.len - half, w.t * w.len));
  planDoc.items.push({ k: kind, a: [w.it.a[0] + ux * (c - half), w.it.a[1] + uy * (c - half)], b: [w.it.a[0] + ux * (c + half), w.it.a[1] + uy * (c + half)] });
  planDraw();
}
function planErase(pt) {
  let best = null;
  planDoc.items.forEach((it, i) => {
    let d;
    if (it.k === "label") d = Math.hypot(it.p[0] - pt[0], it.p[1] - pt[1]) - 14;
    else {
      const dx = it.b[0] - it.a[0], dy = it.b[1] - it.a[1], L2 = dx * dx + dy * dy || 1;
      const t = Math.max(0, Math.min(1, ((pt[0] - it.a[0]) * dx + (pt[1] - it.a[1]) * dy) / L2));
      d = Math.hypot(it.a[0] + dx * t - pt[0], it.a[1] + dy * t - pt[1]);
    }
    if (d < 14 && (!best || d < best.d)) best = { i, d };
  });
  if (best) { planDoc.items.splice(best.i, 1); planDraw(); }
}
function planDraw() {
  const extra = [];
  if (planFrom && planAt) {
    extra.push(`<line class="fp-ghost" x1="${planFrom[0]}" y1="${planFrom[1]}" x2="${planAt[0]}" y2="${planAt[1]}"/>`);
    const mx = (planFrom[0] + planAt[0]) / 2, my = (planFrom[1] + planAt[1]) / 2;
    extra.push(`<text class="fp-meas" x="${mx.toFixed(0)}" y="${(my - 12).toFixed(0)}" text-anchor="middle">${esc(planShow(planDoc, planLen(planDoc, planFrom, planAt)))}</text>`);
  }
  if (planDrag && planAt) {
    const x = Math.min(planDrag[0], planAt[0]), y = Math.min(planDrag[1], planAt[1]);
    const w = Math.abs(planAt[0] - planDrag[0]), h = Math.abs(planAt[1] - planDrag[1]);
    extra.push(`<rect class="fp-ghost" fill="none" x="${x}" y="${y}" width="${w}" height="${h}"/>`);
    extra.push(`<text class="fp-meas" x="${(x + w / 2).toFixed(0)}" y="${(y - 8).toFixed(0)}" text-anchor="middle">${esc(planShow(planDoc, w / planDoc.step * planDoc.perStep))} × ${esc(planShow(planDoc, h / planDoc.step * planDoc.perStep))}</text>`);
  }
  if (planAt) extra.push(`<circle class="fp-snap" cx="${planAt[0]}" cy="${planAt[1]}" r="6"/>`);
  planEl().innerHTML = planSvg(planDoc, { grid: true, measure: true }) + extra.join("");
  planLenBox();
}
// The box over the last wall: its length, ready to be typed over.
function planLenBox() {
  const box = $("planLen"), it = planLast;
  if (!it || planTool !== "wall" || planFrom) { box.hidden = true; return; }
  const svg = planEl(), r = svg.getBoundingClientRect(), host = $("planCanvas").getBoundingClientRect();
  const mx = (it.a[0] + it.b[0]) / 2, my = (it.a[1] + it.b[1]) / 2;
  box.hidden = false;
  box.style.left = (r.left - host.left + mx / planDoc.width * r.width - 46) + "px";
  box.style.top = (r.top - host.top + my / planDoc.height * r.height + 10) + "px";
  if (document.activeElement !== box) box.value = planLen(planDoc, it.a, it.b).toFixed(1);
}
// Typing a length moves the far end of that wall, keeping its direction.
function planApplyLen() {
  const v = parseFloat($("planLen").value);
  if (!planLast || !(v > 0)) return;
  const px = planPx(planDoc, v);
  const dx = planLast.b[0] - planLast.a[0], dy = planLast.b[1] - planLast.a[1], len = Math.hypot(dx, dy) || 1;
  const was = planLast.b;
  const now = [planLast.a[0] + dx / len * px, planLast.a[1] + dy / len * px];
  planLast.b = now;
  // Whatever else met at that corner moves with it, so the walls stay joined.
  const same = q => q && Math.hypot(q[0] - was[0], q[1] - was[1]) < .5;
  for (const it of planDoc.items) {
    if (it === planLast || it.k === "label") continue;
    if (same(it.a)) it.a = [now[0], now[1]];
    if (same(it.b)) it.b = [now[0], now[1]];
  }
  if (planFrom && same(planFrom)) planFrom = [now[0], now[1]];
  planDraw();
  $("planLen").blur();
}

function planOpen(floor) {
  planTarget = floor || null;
  planFrom = planAt = planDrag = planLast = null;
  planTool = "wall";
  const start = plan => {
    planDoc = plan || newPlan();
    $("planPer").value = planDoc.perStep;
    $("planUnit").value = planDoc.unit;
    document.querySelectorAll(".plan-tool[data-tool]").forEach(b => b.classList.toggle("on", b.dataset.tool === "wall"));
    $("planHint").textContent = PLAN_HINTS.wall;
    $("planEd").hidden = false;
    planEl().setAttribute("viewBox", `0 0 ${planDoc.width} ${planDoc.height}`);
    planStartSize = [planDoc.width, planDoc.height];
    planSizeLabel();
    // Fitted to the pane to begin with, so a big plan doesn't open off-screen.
    requestAnimationFrame(() => { planFit(); planDraw(); });
    planDraw();
  };
  if (floor) loadPlan(floor).then(pl => start(pl ? JSON.parse(JSON.stringify(pl)) : null));
  else start(null);
}
function planClose() { $("planEd").hidden = true; planDoc = null; planTarget = null; }

document.querySelectorAll(".plan-tool[data-tool]").forEach(b => {
  b.onclick = () => {
    planTool = b.dataset.tool;
    planFrom = planDrag = null;
    document.querySelectorAll(".plan-tool[data-tool]").forEach(x => x.classList.toggle("on", x === b));
    $("planHint").textContent = PLAN_HINTS[planTool] || "";
    planDraw();
  };
});
document.getElementById("planBigger").onclick = () => planDoc && planResize(1);
document.getElementById("planSmaller").onclick = () => planDoc && planResize(-1);
document.getElementById("planZoomIn").onclick = () => planDoc && planSetZoom(planZoom * 1.25);
document.getElementById("planZoomOut").onclick = () => planDoc && planSetZoom(planZoom / 1.25);
document.getElementById("planFit").onclick = () => { if (planDoc) { planFit(); planDraw(); } };
// Ctrl + wheel zooms, the way a drawing program does; a plain wheel scrolls.
document.getElementById("planCanvas").addEventListener("wheel", e => {
  if (!planDoc || !e.ctrlKey) return;
  e.preventDefault();
  planSetZoom(planZoom * (e.deltaY < 0 ? 1.12 : 1 / 1.12));
}, { passive: false });
document.getElementById("planUndo").onclick = () => { if (!planDoc) return; const it = planDoc.items.pop(); if (planLast === it) planLast = null; planFrom = null; planDraw(); };
document.getElementById("planPer").oninput = () => { if (planDoc) { planDoc.perStep = Math.max(.1, Math.min(100, parseFloat($("planPer").value) || 1)); planSizeLabel(); planDraw(); } };
document.getElementById("planUnit").onchange = () => { if (planDoc) { planDoc.unit = $("planUnit").value; planSizeLabel(); planDraw(); } };
document.getElementById("planCancel").onclick = () => planClose();
document.getElementById("planLen").onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); planApplyLen(); } if (e.key === "Escape") $("planLen").blur(); };
document.getElementById("planLen").onchange = () => planApplyLen();

planEl().addEventListener("pointerdown", e => {
  if (!planDoc) return;
  const pt = planPoint(e);
  if (planTool === "room") { planDrag = planSnap(pt, null); planAt = planDrag; planDraw(); return; }
  if (planTool === "door" || planTool === "window") { planOpening(pt, planTool); return; }
  if (planTool === "erase") { planErase(pt); return; }
  if (planTool === "label") {
    const t = prompt("Name this room (e.g. Kitchen):", "");
    if (t && t.trim()) { planDoc.items.push({ k: "label", p: planSnap(pt, null), t: t.trim().slice(0, 40) }); planDraw(); }
    return;
  }
  const at = planSnap(pt, planFrom);
  if (!planFrom) { planFrom = at; planAt = at; planDraw(); return; }
  if (Math.hypot(at[0] - planFrom[0], at[1] - planFrom[1]) > 3) {
    const it = { k: "wall", a: planFrom, b: at };
    planDoc.items.push(it);
    planLast = it;
    planFrom = at; // keep going from that corner
  }
  planDraw();
});
planEl().addEventListener("pointermove", e => {
  if (!planDoc) return;
  planAt = planSnap(planPoint(e), planTool === "wall" ? planFrom : null);
  if (planFrom || planDrag || planTool === "wall") planDraw();
});
planEl().addEventListener("pointerup", e => {
  if (!planDoc || planTool !== "room" || !planDrag) return;
  const b = planSnap(planPoint(e), null), a = planDrag;
  planDrag = null;
  if (Math.abs(b[0] - a[0]) > 8 && Math.abs(b[1] - a[1]) > 8) {
    const c = [[a[0], a[1]], [b[0], a[1]], [b[0], b[1]], [a[0], b[1]]];
    for (let i = 0; i < 4; i++) planDoc.items.push({ k: "wall", a: c[i], b: c[(i + 1) % 4] });
    planLast = null;
  }
  planDraw();
});
planEl().addEventListener("dblclick", () => { planFrom = null; planDraw(); });
document.addEventListener("keydown", e => {
  if ($("planEd").hidden || e.target === $("planLen")) return;
  if (e.key === "Escape") { e.preventDefault(); if (planFrom) { planFrom = null; planDraw(); } else planClose(); }
  if ((e.key === "z" && (e.ctrlKey || e.metaKey)) || e.key === "Backspace") { e.preventDefault(); $("planUndo").click(); }
});

document.getElementById("planSave").onclick = async () => {
  if (!planDoc) return;
  if (!planDoc.items.length) { toast("Draw a wall or two first"); return; }
  const body = JSON.stringify({ v: 1, width: planDoc.width, height: planDoc.height, unit: planDoc.unit, step: planDoc.step, perStep: planDoc.perStep,
    items: planDoc.items.map(i => i.k === "label" ? { k: "label", p: i.p, t: i.t } : { k: i.k, a: i.a, b: i.b }) });
  const url = planTarget ? `/api/floors/${planTarget.id}/plan` : "/api/floors/plan?name=" + encodeURIComponent(planTarget ? "" : (planDoc.name || ""));
  const r = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body });
  const d = await r.json().catch(() => ({}));
  if (!r.ok) { toast(d.error || "Couldn't save that plan"); return; }
  // A device's spot is a share of the plan, so growing the area would slide
  // every pin across the walls. Move them back by the same ratio.
  if (planTarget && planStartSize && (planStartSize[0] !== planDoc.width || planStartSize[1] !== planDoc.height)) {
    const fx = planStartSize[0] / planDoc.width, fy = planStartSize[1] / planDoc.height;
    for (const pl of (floorData?.places || []).filter(q => q.floorId === planTarget.id)) {
      const x = Math.min(1, Math.max(0, pl.x * fx)), y = Math.min(1, Math.max(0, pl.y * fy));
      await fetch(`/api/floors/${planTarget.id}/places`, { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ hostId: pl.hostId, x, y }) });
      pl.x = x; pl.y = y;
    }
  }
  if (!planTarget && d.id) { floorId = d.id; try { localStorage.setItem("bamf-floor", d.id); } catch { } floorEditing = true; }
  planCache.clear();
  planClose();
  await loadFloors(true);
  renderFloor();
  toast(planTarget ? "Plan saved; the devices kept their spots" : "Floor drawn: pick a device on the right, then click where it goes");
};
document.getElementById("floorDraw").onclick = () => {
  const name = prompt("Name this floor (e.g. Ground floor, Upstairs, Garage):", floorData && floorData.floors.length ? "" : "Ground floor");
  if (name === null) return;
  planOpen(null);
  planDoc.name = name;
};
document.getElementById("floorRedraw").onclick = () => {
  const f = floorData.floors.find(x => x.id === floorId);
  if (f) planOpen(f);
};
document.getElementById("floorReplace").onclick = () => pickFloorImage(async (file, w, h) => {
  const r = await fetch(`/api/floors/${floorId}?width=${w}&height=${h}`, { method: "POST", headers: { "Content-Type": file.type || "application/octet-stream" }, body: file });
  const d = await r.json().catch(() => ({}));
  if (!r.ok) { toast(d.error || "Couldn't replace the image"); return; }
  await loadFloors(true); renderFloor(); toast("Image replaced; the devices kept their spots");
});
document.getElementById("floorRename").onclick = async () => {
  const f = floorData.floors.find(x => x.id === floorId);
  const name = prompt("Rename this floor:", f ? f.name : "");
  if (!name) return;
  const r = await fetch(`/api/floors/${floorId}?name=${encodeURIComponent(name)}`, { method: "POST" });
  if (!r.ok) { toast("Couldn't rename it"); return; }
  await loadFloors(true); renderFloor();
};
document.getElementById("floorDelete").onclick = async () => {
  const f = floorData.floors.find(x => x.id === floorId);
  if (!f || !confirm(`Delete "${f.name}"? Its devices come off the plan; nothing else is touched.`)) return;
  const r = await fetch(`/api/floors/${floorId}`, { method: "DELETE" });
  if (!r.ok) { toast("Couldn't delete it"); return; }
  floorId = null; floorEditing = false;
  await loadFloors(true); renderFloor();
};

