// The Activity tab's cards, arranged by whoever is looking: dragged into the order they want, folded to just their heading, or hidden. Kept in
// this browser only (localStorage), so it changes nothing for anyone else and nothing on the server. The rules for what a saved layout means
// are plain functions at the top, which the tests run; the page wiring below them needs a document and is skipped without one.

const CARD_LAYOUT_KEY = "bamf-activity-layout";

/**
 * A saved layout cleaned up against the cards that exist: only known ids, each once, and the three lists as arrays. A card that is hidden
 * isn't also listed as folded (it has no heading to fold).
 */
function cleanCardLayout(raw, knownIds) {
  const known = new Set(knownIds);
  const list = v => [...new Set(Array.isArray(v) ? v.filter(x => typeof x === "string" && known.has(x)) : [])];
  const hidden = list(raw && raw.hidden);
  return { order: list(raw && raw.order), hidden, folded: list(raw && raw.folded).filter(id => !hidden.includes(id)) };
}

/**
 * The order the cards go in: the saved order for the cards it names, and any card it doesn't know (one added by an update since it was saved)
 * in the place the page gives it: straight after the card before it in the default order, or first if it opens the default order.
 */
function mergeCardOrder(defaultIds, savedOrder) {
  const saved = (savedOrder || []).filter(id => defaultIds.includes(id));
  const out = [...new Set(saved)];
  defaultIds.forEach((id, i) => {
    if (out.includes(id)) return;
    const prev = i > 0 ? out.indexOf(defaultIds[i - 1]) : -1;
    out.splice(prev + 1, 0, id);
  });
  return out;
}

/** The order with one card put just before another (or last, with no `beforeId`). */
function moveCardBefore(order, id, beforeId) {
  const rest = order.filter(x => x !== id);
  const at = beforeId == null ? rest.length : rest.indexOf(beforeId);
  rest.splice(at < 0 ? rest.length : at, 0, id);
  return rest;
}

/** The order with one card moved a place earlier (dir -1) or later (dir 1), or as it was at either end. */
function stepCard(order, id, dir) {
  const i = order.indexOf(id), j = i + dir;
  if (i < 0 || j < 0 || j >= order.length) return order.slice();
  const out = order.slice();
  [out[i], out[j]] = [out[j], out[i]];
  return out;
}

// ---------------------------------------------------------------- the page

function initCardLayout() {
  const wrap = document.getElementById("actCards");
  if (!wrap) return;
  const cards = () => [...wrap.querySelectorAll(":scope > .act-card")];
  const defaults = cards().map(c => c.id).filter(Boolean);
  const readSaved = () => { try { return JSON.parse(localStorage.getItem(CARD_LAYOUT_KEY) || "null"); } catch { return null; } };
  let layout = cleanCardLayout(readSaved(), defaults);
  let editing = false;
  const save = () => {
    try {
      if (!layout.order.length && !layout.hidden.length && !layout.folded.length) localStorage.removeItem(CARD_LAYOUT_KEY);
      else localStorage.setItem(CARD_LAYOUT_KEY, JSON.stringify(layout));
    } catch { /* private mode: it lasts until the page is closed */ }
  };
  const byId = id => document.getElementById(id);
  const titleOf = c => { const h = c.querySelector("h3"); return h ? [...h.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join("").trim() || c.id : c.id; };

  // The strip along the top: arrange, and what is hidden.
  const bar = document.createElement("div");
  bar.className = "act-bar";
  bar.innerHTML = `<span class="act-bar-note" id="actBarNote"></span>
    <button type="button" class="toggle" id="actReset" hidden>Reset layout</button>
    <button type="button" class="toggle" id="actArrange" aria-pressed="false" title="Drag the cards into the order you want, fold them to their heading, or hide them">Arrange cards</button>`;
  wrap.prepend(bar);

  // A strip of controls at the top of each card, shown only while arranging; and a fold button in front of each heading, always.
  for (const c of cards()) {
    if (!c.id) continue;
    const head = c.querySelector("h3");
    if (head && !head.querySelector(".act-fold")) {
      const f = document.createElement("button");
      f.type = "button"; f.className = "act-fold"; f.setAttribute("aria-label", "Fold or open this card");
      f.dataset.card = c.id;
      head.prepend(f);
    }
    const ctl = document.createElement("div");
    ctl.className = "act-editbar";
    ctl.innerHTML = `<button type="button" class="act-handle" data-card="${c.id}" title="Drag to move; the arrow keys move it too" aria-label="Move this card">⠿ Move</button>
      <button type="button" class="toggle" data-act="earlier" data-card="${c.id}" aria-label="Move earlier">◀</button>
      <button type="button" class="toggle" data-act="later" data-card="${c.id}" aria-label="Move later">▶</button>
      <button type="button" class="toggle" data-act="hide" data-card="${c.id}"></button>`;
    c.prepend(ctl);
  }

  function apply() {
    const order = mergeCardOrder(defaults, layout.order);
    for (const id of order) { const c = byId(id); if (c) wrap.appendChild(c); }      // the bar stays first: it is prepended once and the rest are appended after
    wrap.prepend(bar);
    for (const c of cards()) {
      const hidden = layout.hidden.includes(c.id), folded = layout.folded.includes(c.id);
      c.classList.toggle("act-off", hidden);
      c.classList.toggle("act-folded", folded && !hidden);
      const f = c.querySelector(".act-fold");
      if (f) { f.setAttribute("aria-expanded", String(!(folded && !hidden))); f.title = folded ? "Open this card" : "Fold this card to its heading"; }
      const hb = c.querySelector(".act-editbar [data-act=hide]");
      if (hb) hb.textContent = hidden ? "Show" : "Hide";
    }
    wrap.classList.toggle("act-editing", editing);
    const nHidden = layout.hidden.length;
    $("actBarNote").textContent = editing
      ? "Drag a card by its Move handle, or use the arrows; Hide takes it off the tab (it stays here, dimmed, until you leave). Kept in this browser."
      : nHidden ? `${nHidden} card${nHidden === 1 ? "" : "s"} hidden` : "";
    $("actArrange").textContent = editing ? "Done" : "Arrange cards";
    $("actArrange").setAttribute("aria-pressed", String(editing));
    $("actReset").hidden = !editing || !(layout.order.length || nHidden || layout.folded.length);
  }
  const changed = () => { save(); apply(); };
  const current = () => mergeCardOrder(defaults, layout.order);

  wrap.addEventListener("click", ev => {
    const fold = ev.target.closest(".act-fold");
    if (fold) {
      const id = fold.dataset.card;
      layout.folded = layout.folded.includes(id) ? layout.folded.filter(x => x !== id) : [...layout.folded, id];
      changed(); return;
    }
    const b = ev.target.closest("[data-act]");
    if (!b) return;
    const id = b.dataset.card;
    if (b.dataset.act === "hide") {
      layout.hidden = layout.hidden.includes(id) ? layout.hidden.filter(x => x !== id) : [...layout.hidden, id];
      layout.folded = layout.folded.filter(x => !layout.hidden.includes(x));
    } else layout.order = stepCard(current(), id, b.dataset.act === "earlier" ? -1 : 1);
    changed();
  });
  $("actArrange").onclick = () => { editing = !editing; apply(); };
  $("actReset").onclick = () => { if (!confirm("Put the cards back in their original order and show them all?")) return; layout = { order: [], hidden: [], folded: [] }; changed(); toast("Activity cards put back"); };

  // The arrow keys on a focused Move handle.
  wrap.addEventListener("keydown", ev => {
    const h = ev.target.closest && ev.target.closest(".act-handle");
    if (!h || !["ArrowLeft", "ArrowUp", "ArrowRight", "ArrowDown"].includes(ev.key)) return;
    ev.preventDefault();
    layout.order = stepCard(current(), h.dataset.card, ev.key === "ArrowLeft" || ev.key === "ArrowUp" ? -1 : 1);
    changed();
    const again = wrap.querySelector(`.act-handle[data-card="${h.dataset.card}"]`); if (again) again.focus();
  });

  // Dragging: the Move handle makes its card draggable for the length of the drag; where it lands depends on which half of the card under the
  // pointer it is over (above or left of the middle puts it before).
  let dragging = null;
  const clearMarks = () => wrap.querySelectorAll(".act-drop-before, .act-drop-after").forEach(c => c.classList.remove("act-drop-before", "act-drop-after"));
  wrap.addEventListener("pointerdown", ev => { const h = ev.target.closest(".act-handle"); if (h) { const c = byId(h.dataset.card); if (c) c.draggable = true; } });
  wrap.addEventListener("dragstart", ev => {
    const c = ev.target.closest && ev.target.closest(".act-card");
    if (!c || !editing || !c.draggable) { ev.preventDefault(); return; }
    dragging = c.id; c.classList.add("act-dragging");
    ev.dataTransfer.effectAllowed = "move"; try { ev.dataTransfer.setData("text/plain", c.id); } catch { /* ignore */ }
  });
  const before = (ev, c) => { const r = c.getBoundingClientRect(); return (ev.clientX - r.left) / r.width + (ev.clientY - r.top) / r.height < 1; };
  wrap.addEventListener("dragover", ev => {
    if (!dragging) return;
    const c = ev.target.closest(".act-card");
    if (!c || c.id === dragging) return;
    ev.preventDefault(); ev.dataTransfer.dropEffect = "move";
    clearMarks(); c.classList.add(before(ev, c) ? "act-drop-before" : "act-drop-after");
  });
  wrap.addEventListener("drop", ev => {
    if (!dragging) return;
    const c = ev.target.closest(".act-card");
    if (!c || c.id === dragging) return;
    ev.preventDefault();
    const order = current();
    const target = before(ev, c) ? c.id : order[order.indexOf(c.id) + 1] ?? null;
    layout.order = moveCardBefore(order, dragging, target);
    changed();
  });
  wrap.addEventListener("dragend", () => {
    if (dragging) { const c = byId(dragging); if (c) { c.draggable = false; c.classList.remove("act-dragging"); } }
    dragging = null; clearMarks();
  });

  apply();
}
if (typeof document !== "undefined") initCardLayout();
