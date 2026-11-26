// Named layouts for a Map: save where everything sits under a name, switch between arrangements of the same map and come back.
// A layout is only a set of places (the same keys the map saves when you drag), kept on the server so every browser has them.
// Switching to one is saved and drawn like any other arrange (Tidy up, Compact), so it slides if that is on and Undo puts it back.
// layoutPlan() and layoutIsCurrent() decide what a switch changes; the rest draws the menu and talks to the server.

// The places to save so the map ends up as the layout has it: each thing the layout has goes where it says, and a thing placed now that
// the layout has no place for goes back to its automatic one (null). Things the layout has that are no longer on the map (a forgotten
// device, a deleted switch) are left out and counted. Only what would actually move is returned.
function layoutPlan(layout, saved, known) {
  const positions = {};
  let missing = 0;
  for (const [k, p] of Object.entries(layout || {})) {
    if (known && !known.has(k)) { missing++; continue; }
    const now = saved[k];
    if (!now || Math.abs(now[0] - p[0]) > .5 || Math.abs(now[1] - p[1]) > .5) positions[k] = [p[0], p[1]];
  }
  for (const k of Object.keys(saved || {})) if (!(layout && k in layout)) positions[k] = null;
  return { positions, missing };
}

// Whether the map is laid out as this layout has it right now, so the menu can say which one you are on.
function layoutIsCurrent(layout, saved) {
  const a = Object.keys(layout || {}), b = Object.keys(saved || {});
  return a.length > 0 && a.length === b.length && a.every(k => saved[k] && Math.abs(saved[k][0] - layout[k][0]) <= .5 && Math.abs(saved[k][1] - layout[k][1]) <= .5);
}

async function layoutsLoad(subnet) {
  const r = await fetch("/api/map/layouts?subnet=" + encodeURIComponent(subnet));
  if (!r.ok) throw new Error("HTTP " + r.status);
  return r.json();
}

async function layoutSave(subnet, existing) {
  const placed = mapPositions[subnet] || {};
  if (!Object.keys(placed).length) { toast("Nothing on this map has been moved yet. Arrange it the way you want, then save the layout."); return false; }
  const name = (prompt("Name this layout (for example Rack view or For printing):", "") || "").trim();
  if (!name) return false;
  if (existing.some(l => l.name.toLowerCase() === name.replace(/\s+/g, " ").toLowerCase()) && !confirm(`Replace the layout "${name}" with how the map is now?`)) return false;
  try {
    const r = await fetch("/api/map/layouts", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ subnet, name, positions: placed }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || `Couldn't save it (HTTP ${r.status})`)); return false; }
    toast(`Saved the layout “${esc(name)}”`);
    return true;
  } catch { toast("Couldn't reach BAMF to save the layout."); return false; }
}

async function layoutDelete(subnet, l) {
  if (!confirm(`Delete the layout "${l.name}"? The map itself isn't changed.`)) return false;
  try {
    const r = await fetch(`/api/map/layouts?subnet=${encodeURIComponent(subnet)}&name=${encodeURIComponent(l.name)}`, { method: "DELETE" });
    if (!r.ok) { toast("Couldn't delete it."); return false; }
    return true;
  } catch { toast("Couldn't reach BAMF."); return false; }
}

async function layoutApply(subnet, l) {
  const m = topoModels[subnet];
  if (!m) return;
  const { positions, missing } = layoutPlan(l.positions, mapPositions[subnet] || {}, new Set(m.nodes.keys()));
  if (!Object.keys(positions).length) { toast(`The map is already laid out as “${esc(l.name)}”.`); return; }
  await topoCommit(subnet, positions, "Switched layout", false,
    `Switched to “${esc(l.name)}”` + (missing ? ` (${missing} thing${missing === 1 ? "" : "s"} in it ${missing === 1 ? "is" : "are"} no longer on the map)` : ""));
}

// The Layouts button and its menu, beside Arrange. The list is fetched each time the menu opens, so another browser's saves show.
function layoutsMenu(c) {
  const wrap = document.createElement("span");
  wrap.className = "tools-menu-wrap arr-wrap lay-wrap edit-only";
  const open = document.createElement("button");
  open.className = "toggle"; open.type = "button"; open.textContent = "Layouts ▾";
  open.title = "Save how this map is arranged under a name, and switch between saved arrangements";
  open.setAttribute("aria-haspopup", "menu"); open.setAttribute("aria-expanded", "false");
  const menu = document.createElement("div");
  menu.className = "tools-menu arr-menu lay-menu";
  menu.setAttribute("role", "menu");
  const close = back => { menu.classList.remove("show"); open.setAttribute("aria-expanded", "false"); if (back) open.focus(); };

  const draw = list => {
    menu.textContent = "";
    const save = document.createElement("button");
    save.type = "button"; save.className = "arr-item"; save.setAttribute("role", "menuitem");
    save.innerHTML = "<b></b><span></span>";
    save.children[0].textContent = "Save this layout…";
    save.children[1].textContent = "Keep where everything sits now under a name. The same name replaces the old one.";
    save.onclick = async () => { close(false); await layoutSave(c.subnet, list); };
    menu.appendChild(save);
    const saved = mapPositions[c.subnet] || {};
    if (!list.length) {
      const none = document.createElement("div");
      none.className = "lay-none";
      none.textContent = "No saved layouts yet.";
      menu.appendChild(none);
    }
    list.forEach((l, i) => {
      const row = document.createElement("div");
      row.className = "lay-row" + (i === 0 ? " first" : "");
      const go = document.createElement("button");
      go.type = "button"; go.className = "arr-item"; go.setAttribute("role", "menuitem");
      go.innerHTML = "<b></b><span></span>";
      const current = layoutIsCurrent(l.positions, saved);
      go.children[0].textContent = (current ? "✓ " : "") + l.name;
      go.children[1].textContent = current ? "This is how the map is now." : `${l.nodes} thing${l.nodes === 1 ? "" : "s"} placed · saved ${fmtAgo(l.created)}`;
      go.title = `Switch the map to “${l.name}”. Undo puts it back.`;
      go.onclick = () => { close(false); layoutApply(c.subnet, l); };
      const del = document.createElement("button");
      del.type = "button"; del.className = "lay-del"; del.setAttribute("role", "menuitem");
      del.textContent = "×"; del.title = `Delete the layout “${l.name}”`; del.setAttribute("aria-label", `Delete the layout ${l.name}`);
      del.onclick = async () => { close(false); await layoutDelete(c.subnet, l); };
      row.append(go, del);
      menu.appendChild(row);
    });
  };

  open.onclick = async e => {
    e.stopPropagation();
    document.querySelectorAll(".arr-menu.show").forEach(x => { if (x !== menu) x.classList.remove("show"); });
    const on = menu.classList.toggle("show");
    open.setAttribute("aria-expanded", on ? "true" : "false");
    if (!on) return;
    const fromKeyboard = e.detail === 0;
    menu.textContent = "Loading…";
    try { draw(await layoutsLoad(c.subnet)); } catch { menu.textContent = "Couldn't load the saved layouts."; return; }
    if (fromKeyboard) { const first = menu.querySelector(".arr-item:not(:disabled)"); if (first) first.focus(); }
  };
  wrap.addEventListener("keydown", e => {
    if (!menu.classList.contains("show")) return;
    if (e.key === "Escape") { e.preventDefault(); close(true); return; }
    const items = [...menu.querySelectorAll(".arr-item:not(:disabled), .lay-del")];
    if (!items.length || !/^Arrow(Down|Up)$|^(Home|End)$/.test(e.key)) return;
    e.preventDefault();
    const i = items.indexOf(document.activeElement);
    const next = e.key === "Home" ? 0 : e.key === "End" ? items.length - 1 : e.key === "ArrowDown" ? (i + 1) % items.length : (i <= 0 ? items.length - 1 : i - 1);
    items[next].focus();
  });
  wrap.append(open, menu);
  return wrap;
}
