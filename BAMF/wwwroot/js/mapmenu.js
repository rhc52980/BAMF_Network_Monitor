// The Map's right-click menu: what you would do to the thing under the pointer, without going to the devices list for it.
// Off until the "Right-click menu" button on the Map toolbar is turned on, and then only on a thing drawn on a topology map; with
// it off the browser's own menu appears as ever. The actions are the ones the device list's "..." menu and a click already
// open, so nothing here is new to learn. mapMenuItems() only decides what is on the menu; the rest of the file draws and runs it.

let mapMenuOn = false;
try { mapMenuOn = localStorage.getItem("bamf-map-menu") === "1"; } catch { }

// What goes on the menu for a thing on map m, as { head: { title, sub, on }, items }: each item is { label, fn, danger } or { sep: true }.
// null when there is nothing to offer, which leaves the browser's own menu in place.
function mapMenuItems(m, key) {
  const n = m.nodes.get(key);
  if (!n) return null;
  const edit = role !== "viewer";
  const items = [];
  const add = (label, fn, o = {}) => items.push({ label, fn, ...o });
  const sep = () => { if (items.length && !items[items.length - 1].sep) items.push({ sep: true }); };
  const copy = async (what, text) => { toast((await copyText(text)) ? `Copied ${esc(what)}: ${esc(text)}` : `Couldn't copy ${esc(what)}`); };
  const saved = m.saved || {};
  const unpin = (keys, label) => async () => {
    const positions = {};
    for (const k of keys) if (saved[k]) positions[k] = null;
    if (Object.keys(positions).length) await topoCommit(m.subnet, positions, label, true);
  };

  // Several things are picked and this is one of them: the menu is for the lot.
  const sel = mapSel[m.subnet];
  if (edit && sel && sel.size > 1 && sel.has(key)) {
    const keys = [...sel], movers = arrPicked(m, keys).length;
    for (const t of SEL_TOOLS) {
      if (!t) { sep(); continue; }
      if (movers >= t[3]) add(t[1], () => topoAlign(m.subnet, t[0]));
    }
    if (keys.some(k => saved[k])) { sep(); add("Put them back in their automatic places", unpin(keys, "Put back in automatic places")); }
    sep();
    add("Let go of the selection", () => topoSelect(m, [], "set"));
    return { head: { title: sel.size + " selected", sub: movers + (movers === 1 ? " thing" : " things") + " to move", on: null }, items };
  }

  if (n.type === "switch") {
    const sw = switches.find(s => s.id === n.sw.id) || n.sw, h = n.host ? hosts.find(x => x.id === n.host.id) || n.host : null;
    add(sw.kind === "virtual" ? "VMs…" : sw.kind === "vpn" ? "Clients…" : sw.kind === "ssid" ? "Devices on it…" : "Ports…", () => openPortsDialog(sw));
    if (edit) {
      if (sw.kind === "ap") add("Add a wireless SSID…", () => openSwitchDialog(null, null, null, { kind: "ssid", runsOn: sw.id }));
      if (sw.kind === "switch" || sw.kind === "router") add("Traffic counters…", () => openSnmpDialog(sw));
      add(`Edit ${kindNoun(sw.kind)}…`, () => openSwitchDialog(sw));
    }
    if (h && !h.synthetic) { sep(); add("Open in the devices list", () => jumpToHost(h.id)); if (h.ip) add("Copy IP address", () => copy("IP address", h.ip)); }
    if (edit && saved[key]) { sep(); add("Put back in its automatic place", unpin([key], "Put back in its automatic place")); }
    return { head: { title: sw.name, sub: kindNoun(sw.kind) + (sw.ports && sw.kind !== "ssid" && sw.kind !== "ap" ? ` · ${sw.ports} ports` : "") + (h && h.ip ? " · " + h.ip : ""), on: h ? !!h.online : null }, items };
  }

  const h = n.host ? hosts.find(x => x.id === n.host.id) || n.host : null;
  if (!h || h.synthetic) return null;
  add("Open in the devices list", () => jumpToHost(h.id));
  if (edit) {
    sep();
    if (n.type === "dev") {
      const plugged = h.switchId ? switches.find(s => s.id === h.switchId) : null;
      add(plugged ? "Change plugged into…" : "Plugged into…", () => openPlugDialog(h));
      // A place the gear itself reported is the gear's to change; one you recorded can be taken back.
      if (plugged && !h.switchAuto) {
        add("Unplug from " + plugged.name, async () => {
          await fetch(`/api/hosts/${h.id}/plug`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ switchId: 0, port: 0 }) });
          toast(`${esc(nameOrIp(h))} is no longer recorded on ${esc(plugged.name)}`);
          mapSig = null;
          await refresh();
        });
      }
    } else if (n.type === "root") {
      add("Gateway…", () => openGatewayDialog(h));
    }
    add("Map icon…", () => openTypeDialog(h));
    add("Device type…", () => openKindDialog(h));
    add(h.note ? "Edit note…" : "Add note…", () => openNoteDialog(h));
    add("Snooze…", () => openSnoozeDialog(h));
    add(h.known ? "Mark as unknown" : "Mark as known", async () => { await setKnown(h, !h.known); await refresh(); });
    add(h.ignored ? "Stop ignoring" : "Ignore", async () => { await setIgnored(h, !h.ignored); await refresh(); });
  }
  sep();
  if (!h.remote) add("Show in 3D", () => showView("3d/" + h.id));
  if (edit && networkToolsEnabled && !h.remote) {
    add("Ping…", () => openTools(h, "ping"));
    add("Trace route…", () => openTools(h, "trace"));
  }
  sep();
  if (h.ip) add("Copy IP address", () => copy("IP address", h.ip));
  if (h.mac) add("Copy MAC address", () => copy("MAC address", h.mac));
  if (edit && saved[key]) { sep(); add("Put back in its automatic place", unpin([key], "Put back in its automatic place")); }
  while (items.length && items[items.length - 1].sep) items.pop();
  return { head: { title: nameOrIp(h), sub: [h.ip, h.online ? "online" : "offline"].filter(Boolean).join(" · "), on: !!h.online }, items };
}

// ---- Drawing it ----
let mapMenuEl = null, mapMenuFrom = null;

function mapMenuClose(refocus) {
  if (!mapMenuEl) return;
  mapMenuEl.remove();
  mapMenuEl = null;
  document.removeEventListener("pointerdown", mapMenuOutside, true);
  window.removeEventListener("resize", mapMenuClose);
  window.removeEventListener("blur", mapMenuClose);
  document.removeEventListener("scroll", mapMenuScroll, true);
  const from = mapMenuFrom;
  mapMenuFrom = null;
  if (refocus === true && from) {
    const el = [...document.querySelectorAll(".tnode")].find(g => g.getAttribute("data-key") === from.key && g.closest(".map-card")?.dataset.subnet === from.subnet);
    if (el) el.focus({ preventScroll: true });
  }
}
function mapMenuOutside(e) { if (mapMenuEl && !mapMenuEl.contains(e.target)) mapMenuClose(); }
function mapMenuScroll(e) { if (mapMenuEl && !mapMenuEl.contains(e.target)) mapMenuClose(); }

function mapMenuOpen(spec, x, y, from) {
  mapMenuClose();
  const menu = document.createElement("div");
  menu.className = "row-menu rm-device show map-menu";
  menu.setAttribute("role", "menu");
  menu.setAttribute("aria-label", "Actions for " + spec.head.title);
  const head = document.createElement("div");
  head.className = "rm-head";
  if (spec.head.on !== null) { const dot = document.createElement("span"); dot.className = "rm-dot" + (spec.head.on ? " on" : ""); head.appendChild(dot); }
  const who = document.createElement("div"), nm = document.createElement("b"), sm = document.createElement("small");
  nm.textContent = spec.head.title; sm.textContent = spec.head.sub;
  who.append(nm, sm); head.appendChild(who);
  menu.appendChild(head);
  const buttons = [];
  for (const it of spec.items) {
    if (it.sep) { const s = document.createElement("div"); s.className = "mm-sep"; s.setAttribute("role", "separator"); menu.appendChild(s); continue; }
    const b = document.createElement("button");
    b.type = "button"; b.setAttribute("role", "menuitem"); b.tabIndex = -1; b.textContent = it.label;
    if (it.danger) b.classList.add("danger");
    b.onclick = async e => {
      e.stopPropagation();
      mapMenuClose();
      try { await it.fn(); } catch (err) { console.error("map menu action failed", err); toast("That didn't work: " + esc(err && err.message || err)); }
    };
    menu.appendChild(b);
    buttons.push(b);
  }
  menu.addEventListener("keydown", e => {
    const i = buttons.indexOf(document.activeElement);
    const go = k => { e.preventDefault(); buttons[(k + buttons.length) % buttons.length].focus(); };
    if (e.key === "ArrowDown") go(i + 1);
    else if (e.key === "ArrowUp") go(i < 0 ? -1 : i - 1);
    else if (e.key === "Home") go(0);
    else if (e.key === "End") go(-1);
    else if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); mapMenuClose(true); }
    else if (e.key === "Tab") { mapMenuClose(true); }
  });
  menu.addEventListener("contextmenu", e => e.preventDefault());
  document.body.appendChild(menu);
  mapMenuEl = menu; mapMenuFrom = from;
  // Where the pointer is, moved back inside the window if it would hang over an edge.
  const r = menu.getBoundingClientRect(), vw = window.innerWidth, vh = window.innerHeight;
  menu.style.left = Math.max(8, Math.min(x, vw - r.width - 8)) + "px";
  menu.style.top = Math.max(8, Math.min(y, vh - r.height - 8)) + "px";
  document.addEventListener("pointerdown", mapMenuOutside, true);
  window.addEventListener("resize", mapMenuClose);
  window.addEventListener("blur", mapMenuClose);
  document.addEventListener("scroll", mapMenuScroll, true);
  if (buttons[0]) buttons[0].focus({ preventScroll: true });
}

// The browser's menu key and Shift+F10 arrive here too, at the focused thing.
document.addEventListener("contextmenu", e => {
  if (!mapMenuOn || mapDragging) return;
  const g = e.target.closest && e.target.closest(".map-svg.topo .tnode");
  if (!g) return;
  const card = g.closest(".map-card"), m = card ? topoModels[card.dataset.subnet] : null;
  if (!m) return;
  const key = g.getAttribute("data-key");
  const spec = mapMenuItems(m, key);
  if (!spec || !spec.items.length) return;
  e.preventDefault();
  let x = e.clientX, y = e.clientY;
  if (!x && !y) { const r = g.getBoundingClientRect(); x = r.left + r.width / 2; y = r.top + r.height / 2; }
  mapMenuOpen(spec, x, y, { subnet: m.subnet, key });
});
