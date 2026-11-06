// The Activity tab's cards, arranged by whoever is looking: dragged into the order they want, folded to just their heading, or hidden. Kept in
// this browser only (localStorage), so it changes nothing for anyone else and nothing on the server. The rules for what a saved layout means
// are plain functions at the top, which the tests run; the page wiring below them needs a document and is skipped without one.

const CARD_LAYOUT_KEY = "bamf-activity-layout";
const SETTINGS_LAYOUT_KEY = "bamf-settings-layout";

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

/**
 * Makes the cards inside one element arrangeable. The same code runs the Activity tab and each Settings section; what differs is in `cfg`:
 *   wrap      the element holding the cards
 *   key       the localStorage key its layout is kept under
 *   cardSel   which of its children are the cards
 *   headSel   the heading inside a card, which the fold button goes in front of
 *   idOf      a card's stable id, given the card and its position (a card without one can't be arranged)
 *   canHide   whether a card can be taken off the page. Settings can't: a setting that can't be found is worse than one in the wrong place
 *   tailOf    an element that moves with a card (the Save and Reset that sit under the networks), or null
 */
function setupCardLayout(cfg) {
  const wrap = cfg.wrap;
  const cards = () => [...wrap.querySelectorAll(cfg.cardSel)];
  cards().forEach((c, i) => { const id = cfg.idOf(c, i); if (id) c.dataset.layId = id; });
  const defaults = cards().map(c => c.dataset.layId).filter(Boolean);
  if (defaults.length < 2) return;
  const tails = new Map(defaults.map(id => [id, cfg.tailOf ? cfg.tailOf(cards().find(c => c.dataset.layId === id)) : null]));
  const readSaved = () => { try { return JSON.parse(localStorage.getItem(cfg.key) || "null"); } catch { return null; } };
  const clean = raw => { const l = cleanCardLayout(raw, defaults); if (!cfg.canHide) l.hidden = []; return l; };
  let layout = clean(readSaved());
  let editing = false;
  const save = () => {
    try {
      if (!layout.order.length && !layout.hidden.length && !layout.folded.length) localStorage.removeItem(cfg.key);
      else localStorage.setItem(cfg.key, JSON.stringify(layout));
    } catch { /* private mode: it lasts until the page is closed */ }
  };
  const byId = id => cards().find(c => c.dataset.layId === id);

  // The strip along the top: arrange, and what is hidden.
  const bar = document.createElement("div");
  bar.className = "act-bar";
  bar.innerHTML = `<span class="act-bar-note"></span>
    <button type="button" class="toggle" data-bar="reset" hidden>Reset layout</button>
    <button type="button" class="toggle" data-bar="arrange" aria-pressed="false" title="Drag the cards into the order you want, fold them to their heading${cfg.canHide ? ", or hide them" : ""}">Arrange cards</button>`;
  wrap.prepend(bar);
  const note = bar.querySelector(".act-bar-note"), resetBtn = bar.querySelector("[data-bar=reset]"), arrangeBtn = bar.querySelector("[data-bar=arrange]");

  // A strip of controls at the top of each card, shown only while arranging; and a fold button in front of each heading, always.
  for (const c of cards()) {
    const id = c.dataset.layId;
    if (!id) continue;
    const head = c.querySelector(cfg.headSel);
    if (head) head.classList.add("act-head");
    if (head && !head.querySelector(".act-fold")) {
      const f = document.createElement("button");
      f.type = "button"; f.className = "act-fold"; f.setAttribute("aria-label", "Fold or open this card");
      f.dataset.card = id;
      head.prepend(f);
    }
    const ctl = document.createElement("div");
    ctl.className = "act-editbar";
    ctl.innerHTML = `<button type="button" class="act-handle" data-card="${id}" title="Drag to move; the arrow keys move it too" aria-label="Move this card">⠿ Move</button>
      <button type="button" class="toggle" data-act="earlier" data-card="${id}" aria-label="Move earlier">◀</button>
      <button type="button" class="toggle" data-act="later" data-card="${id}" aria-label="Move later">▶</button>` +
      (cfg.canHide ? `<button type="button" class="toggle" data-act="hide" data-card="${id}"></button>` : "");
    c.prepend(ctl);
  }

  function apply() {
    const order = mergeCardOrder(defaults, layout.order);
    for (const id of order) { const c = byId(id); if (c) { wrap.appendChild(c); if (tails.get(id)) wrap.appendChild(tails.get(id)); } }   // the bar stays first: it is prepended once and the rest are appended after
    wrap.prepend(bar);
    for (const c of cards()) {
      const hidden = layout.hidden.includes(c.dataset.layId), folded = layout.folded.includes(c.dataset.layId);
      c.classList.toggle("act-off", hidden);
      c.classList.toggle("act-folded", folded && !hidden);
      const f = c.querySelector(".act-fold");
      if (f) { f.setAttribute("aria-expanded", String(!(folded && !hidden))); f.title = folded ? "Open this card" : "Fold this card to its heading"; }
      const hb = c.querySelector(".act-editbar [data-act=hide]");
      if (hb) hb.textContent = hidden ? "Show" : "Hide";
    }
    wrap.classList.toggle("act-editing", editing);
    const nHidden = layout.hidden.length;
    note.textContent = editing
      ? (cfg.canHide ? "Drag a card by its Move handle, or use the arrows; Hide takes it off the tab (it stays here, dimmed, until you leave). Kept in this browser."
        : "Drag a card by its Move handle, or use the arrows. Kept in this browser.")
      : nHidden ? `${nHidden} card${nHidden === 1 ? "" : "s"} hidden` : "";
    arrangeBtn.textContent = editing ? "Done" : "Arrange cards";
    arrangeBtn.setAttribute("aria-pressed", String(editing));
    resetBtn.hidden = !editing || !(layout.order.length || nHidden || layout.folded.length);
  }
  const changed = () => { save(); apply(); };
  const current = () => mergeCardOrder(defaults, layout.order);

  wrap.addEventListener("click", ev => {
    const fold = ev.target.closest(".act-fold");
    if (fold && wrap.contains(fold)) {
      const id = fold.dataset.card;
      layout.folded = layout.folded.includes(id) ? layout.folded.filter(x => x !== id) : [...layout.folded, id];
      changed(); return;
    }
    const b = ev.target.closest("[data-act]");
    if (!b || !wrap.contains(b)) return;
    const id = b.dataset.card;
    if (b.dataset.act === "hide") {
      layout.hidden = layout.hidden.includes(id) ? layout.hidden.filter(x => x !== id) : [...layout.hidden, id];
      layout.folded = layout.folded.filter(x => !layout.hidden.includes(x));
    } else layout.order = stepCard(current(), id, b.dataset.act === "earlier" ? -1 : 1);
    changed();
  });
  arrangeBtn.onclick = () => { editing = !editing; apply(); };
  resetBtn.onclick = () => {
    if (!confirm("Put the cards back in their original order" + (cfg.canHide ? " and show them all" : "") + "?")) return;
    layout = { order: [], hidden: [], folded: [] }; changed(); toast(cfg.resetToast);
  };

  // The arrow keys on a focused Move handle.
  wrap.addEventListener("keydown", ev => {
    const h = ev.target.closest && ev.target.closest(".act-handle");
    if (!h || !["ArrowLeft", "ArrowUp", "ArrowRight", "ArrowDown"].includes(ev.key)) return;
    ev.preventDefault();
    layout.order = stepCard(current(), h.dataset.card, ev.key === "ArrowLeft" || ev.key === "ArrowUp" ? -1 : 1);
    changed();
    const again = wrap.querySelector(`.act-handle[data-card="${CSS.escape(h.dataset.card)}"]`); if (again) again.focus();
  });

  // Dragging: the Move handle makes its card draggable for the length of the drag; where it lands depends on which half of the card under the
  // pointer it is over (above or left of the middle puts it before).
  let dragging = null;
  const clearMarks = () => wrap.querySelectorAll(".act-drop-before, .act-drop-after").forEach(c => c.classList.remove("act-drop-before", "act-drop-after"));
  const cardOf = ev => { const c = ev.target.closest && ev.target.closest("[data-lay-id]"); return c && wrap.contains(c) ? c : null; };
  wrap.addEventListener("pointerdown", ev => { const h = ev.target.closest(".act-handle"); if (h) { const c = byId(h.dataset.card); if (c) c.draggable = true; } });
  wrap.addEventListener("dragstart", ev => {
    const c = cardOf(ev);
    if (!c || !editing || !c.draggable) { ev.preventDefault(); return; }
    dragging = c.dataset.layId; c.classList.add("act-dragging");
    ev.dataTransfer.effectAllowed = "move"; try { ev.dataTransfer.setData("text/plain", c.dataset.layId); } catch { /* ignore */ }
  });
  const before = (ev, c) => { const r = c.getBoundingClientRect(); return (ev.clientX - r.left) / r.width + (ev.clientY - r.top) / r.height < 1; };
  wrap.addEventListener("dragover", ev => {
    if (!dragging) return;
    const c = cardOf(ev);
    if (!c || c.dataset.layId === dragging) return;
    ev.preventDefault(); ev.dataTransfer.dropEffect = "move";
    clearMarks(); c.classList.add(before(ev, c) ? "act-drop-before" : "act-drop-after");
  });
  wrap.addEventListener("drop", ev => {
    if (!dragging) return;
    const c = cardOf(ev);
    if (!c || c.dataset.layId === dragging) return;
    ev.preventDefault();
    const order = current();
    const target = before(ev, c) ? c.dataset.layId : order[order.indexOf(c.dataset.layId) + 1] ?? null;
    layout.order = moveCardBefore(order, dragging, target);
    changed();
  });
  wrap.addEventListener("dragend", () => {
    if (dragging) { const c = byId(dragging); if (c) { c.draggable = false; c.classList.remove("act-dragging"); } }
    dragging = null; clearMarks();
  });

  apply();
}

/** The Activity tab: every card with an id can be moved, folded or hidden. */
function initCardLayout() {
  const wrap = document.getElementById("actCards");
  if (!wrap) return;
  setupCardLayout({
    wrap, key: CARD_LAYOUT_KEY, cardSel: ":scope > .act-card", headSel: "h3", canHide: true,
    idOf: c => c.id || null, resetToast: "Activity cards put back",
  });
}

/** Settings, a section at a time: its cards can be moved and folded, never hidden, and the Save and Reset under the networks stay with their card. */
function initSettingsLayouts() {
  document.querySelectorAll(".set-sec").forEach(sec => {
    const used = new Set();
    setupCardLayout({
      wrap: sec, key: SETTINGS_LAYOUT_KEY + ":" + sec.dataset.sec, cardSel: ":scope > .set-card", headSel: ".set-card-title", canHide: false,
      idOf: c => {
        const t = c.querySelector(".set-card-title");
        const base = c.id || (t ? t.textContent : "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
        if (!base) return null;
        let id = base, n = 2;
        while (used.has(id)) id = base + "-" + n++;
        used.add(id);
        return id;
      },
      tailOf: c => c.nextElementSibling && c.nextElementSibling.classList.contains("set-actions") ? c.nextElementSibling : null,
      resetToast: "Settings cards put back",
    });
  });
}
if (typeof document !== "undefined") { initCardLayout(); initSettingsLayouts(); }
