// Arranging the topology Map: tidying a layout you've roughed out, reflowing it to the shape of the screen, snapping it to a grid,
// and snapping a node to its neighbours as it's dragged. These work on a drawn map's positions and hand back the ones to save;
// map.js draws and saves them. Nothing here touches the page, so tests/web can run it as it ships.
//
// The unit they move is a block: a switch, access point or "On this network" box under the router, with everything that hangs
// from it (its devices, its SSIDs, a switch chained on). A block moves as one, so what you arranged inside it stays arranged.
// Only the block's own node and the nodes you had already placed by hand are saved; the rest follow their parent, as they do
// everywhere on the Map, so a device that turns up later still lands where it should.

const ARR = {
  TILE: 16,           // half a tile, as TOPO.T
  GAP_X: 70,          // the least gap between two blocks in a row
  GAP_Y: 56,          // the least gap between two rows
  ROW_TOL: 70,        // blocks whose rows are this close are one row
  ALIGN: 60,          // rows that start, or are centred, this close are made to start, or be centred, together
  UNDER: 62,          // room between the router and the first row, for the lines that join them
  MIN_GAP: 24,        // the least gap Distribute leaves between two things
  GRID: [40, 23],     // the snap grid; 23 is half a device row
  RATIO: 1.7,         // the shape Compact aims for when it can't see the frame
};

// parent -> its children, and a node with everything that hangs from it. skipPinned stops at nodes you placed by hand, which
// don't travel with their parent.
function arrKids(m) {
  const kids = new Map();
  for (const [k, n] of m.nodes) {
    if (!n.parent || !m.nodes.has(n.parent)) continue;
    if (!kids.has(n.parent)) kids.set(n.parent, []);
    kids.get(n.parent).push(k);
  }
  return kids;
}
function arrTree(m, kids, key, skipPinned = false, out = []) {
  out.push(key);
  for (const c of kids.get(key) || []) if (!(skipPinned && m.saved[c])) arrTree(m, kids, c, skipPinned, out);
  return out;
}

// The room one node takes: left, top, right, bottom. A name runs to the right of its tile, and a cable arches over a switch.
function arrExtent(m, key) {
  const n = m.nodes.get(key), p = m.final.get(key);
  if (n.type === "box") return [p[0] - 4, p[1] - 4, p[0] + n.size[0] + 4, p[1] + n.size[1] + 4];
  return [p[0] - ARR.TILE - 4, p[1] - ARR.TILE - 30, p[0] + ARR.TILE + 12 + (n.labelW || 120), p[1] + 40];
}
function arrBox(m, keys) {
  let r = [Infinity, Infinity, -Infinity, -Infinity];
  for (const k of keys) {
    const e = arrExtent(m, k);
    r = [Math.min(r[0], e[0]), Math.min(r[1], e[1]), Math.max(r[2], e[2]), Math.max(r[3], e[3])];
  }
  return r;
}

// A block: its box, and its anchor, the height its connection sits at (a box's is its top edge's 24 lower, as it's laid out), so
// that blocks of different kinds line up by where they're joined rather than by where their drawings start.
function arrBlock(m, kids, key) {
  const keys = arrTree(m, kids, key), r = arrBox(m, keys);
  const p = m.final.get(key), ay = m.nodes.get(key).type === "box" ? p[1] + 24 : p[1];
  return { key, keys, r, w: r[2] - r[0], h: r[3] - r[1], ay, off: ay - r[1] };
}

// Each top (a router or gateway) with its own head (the top, its ports, this machine) and its blocks.
function arrUnits(m) {
  const kids = arrKids(m), units = [];
  for (const [top, n] of m.nodes) {
    if (!(n.type === "root" || n.isRoot)) continue;
    const head = [top], blocks = [];
    for (const c of kids.get(top) || []) {
      const t = m.nodes.get(c).type;
      if (t === "switch" || t === "box") blocks.push(arrBlock(m, kids, c));
      else arrTree(m, kids, c, false, head);
    }
    units.push({ top, head, hr: arrBox(m, head), blocks });
  }
  return units.sort((a, b) => a.hr[0] - b.hr[0]);
}

// Move a block: its own node and whatever in it was placed by hand are saved at their new place.
function arrMove(m, keys, dx, dy, out) {
  if (!dx && !dy) return;
  keys.forEach((k, i) => {
    if (i === 0 || m.saved[k]) { const p = m.final.get(k); out[k] = [Math.round(p[0] + dx), Math.round(p[1] + dy)]; }
  });
}

// Units were laid out each on its own; set them side by side, left to right, so none lands on the next.
function arrSideBySide(m, plans, out) {
  let edge = -Infinity;
  for (const pl of plans) {
    const shift = Math.max(0, edge + ARR.GAP_X * 2 - pl.left);
    for (const mv of pl.moves) arrMove(m, mv.keys, mv.dx + shift, mv.dy, out);
    edge = pl.right + shift;
  }
}

// Compact: reflow the blocks into rows so the whole drawing is about the shape of the frame, not one long strip. The blocks keep
// their left-to-right order; the router goes above, centred over the widest row. ratio is the frame's width over its height.
function arrCompact(m, ratio = ARR.RATIO) {
  const plans = [];
  for (const u of arrUnits(m)) {
    const hw = u.hr[2] - u.hr[0], hh = u.hr[3] - u.hr[1];
    const bl = u.blocks.slice().sort((a, b) => a.r[0] - b.r[0] || a.ay - b.ay);
    const left = Math.min(u.hr[0], ...bl.map(b => b.r[0]));
    let best = null, score = Infinity;
    for (let per = 1; per <= Math.max(1, bl.length); per++) {
      const rows = [];
      for (let i = 0; i < bl.length; i += per) rows.push(bl.slice(i, i + per));
      const widths = rows.map(r => r.reduce((s, b) => s + b.w, 0) + ARR.GAP_X * (r.length - 1));
      const W = Math.max(hw, ...widths);
      const H = hh + ARR.UNDER + rows.reduce((s, r) => s + Math.max(...r.map(b => b.h)), 0) + ARR.GAP_Y * Math.max(0, rows.length - 1);
      const s = Math.abs(Math.log(W / H / ratio));
      if (s < score - 1e-9) { score = s; best = { rows, widths, W }; }
    }
    const moves = [], W = best ? best.W : hw;
    moves.push({ keys: u.head, dx: left + (W - hw) / 2 - u.hr[0], dy: 0 });
    let top = u.hr[3] + ARR.UNDER;
    if (best) best.rows.forEach((row, ri) => {
      const anchor = top + Math.max(...row.map(b => b.off));
      let x = left + (W - best.widths[ri]) / 2, bottom = top;
      for (const b of row) {
        moves.push({ keys: b.keys, dx: x - b.r[0], dy: anchor - b.ay });
        bottom = Math.max(bottom, anchor - b.off + b.h);
        x += b.w + ARR.GAP_X;
      }
      top = bottom + ARR.GAP_Y;
    });
    plans.push({ left, right: left + W, moves });
  }
  const out = {};
  arrSideBySide(m, plans, out);
  return arrChanged(m, out);
}

// Tidy up: take the layout as you roughed it out and snap it straight. Blocks that sit about level become one row, every row's
// blocks are spread with even gaps and none overlap, the rows are kept clear of each other and of the router, and the router is
// centred over them. It keeps your rows and your order; it only evens them out.
function arrTidy(m) {
  const plans = [];
  for (const u of arrUnits(m)) {
    const hw = u.hr[2] - u.hr[0];
    const rows = [];
    for (const b of u.blocks.slice().sort((a, c) => a.ay - c.ay || a.r[0] - c.r[0])) {
      const row = rows[rows.length - 1];
      if (row && b.ay - row.ay < ARR.ROW_TOL) { row.items.push(b); row.ay = row.items.reduce((s, x) => s + x.ay, 0) / row.items.length; }
      else rows.push({ ay: b.ay, items: [b] });
    }
    // Each row on its own first: left to right as you had it, the gaps evened out.
    const laid = rows.map(row => {
      const items = row.items.sort((a, c) => a.r[0] - c.r[0]);
      const x0 = items[0].r[0], x1 = Math.max(...items.map(b => b.r[2]));
      const gap = items.length > 1 ? Math.max(ARR.GAP_X, (x1 - x0 - items.reduce((s, b) => s + b.w, 0)) / (items.length - 1)) : 0;
      return { row, items, gap, x0, width: items.reduce((s, b) => s + b.w, 0) + gap * (items.length - 1) };
    });
    // Then the rows against each other, the widest being what the rest are judged by: a row that starts about where it does
    // starts exactly there, and failing that a row centred about where it is centred exactly there.
    const ref = laid.reduce((a, l) => l.width > a.width ? l : a, laid[0]);
    for (const l of laid) {
      if (l === ref) continue;
      if (Math.abs(l.x0 - ref.x0) < ARR.ALIGN) l.x0 = ref.x0;
      else if (Math.abs(l.x0 + l.width / 2 - (ref.x0 + ref.width / 2)) < ARR.ALIGN) l.x0 = ref.x0 + (ref.width - l.width) / 2;
    }
    const moves = [];
    let top = u.hr[3] + 20, left = Math.min(u.hr[0], ...u.blocks.map(b => b.r[0])), right = Math.max(u.hr[2], ...u.blocks.map(b => b.r[2]));
    const placed = [];
    for (const l of laid) {
      // A row can't start higher than the one above it ends, nor than the router's own room.
      const maxOff = Math.max(...l.items.map(b => b.off));
      const anchor = Math.max(Math.round(l.row.ay), top + maxOff);
      let x = l.x0, bottom = anchor;
      for (const b of l.items) {
        moves.push({ keys: b.keys, dx: x - b.r[0], dy: anchor - b.ay });
        placed.push([x, x + b.w]);
        bottom = Math.max(bottom, anchor - b.off + b.h);
        x += b.w + l.gap;
      }
      top = bottom + ARR.GAP_Y;
    }
    if (placed.length) { left = Math.min(...placed.map(p => p[0])); right = Math.max(...placed.map(p => p[1])); }
    moves.push({ keys: u.head, dx: (left + right) / 2 - hw / 2 - u.hr[0], dy: 0 });
    plans.push({ left: Math.min(left, (left + right) / 2 - hw / 2), right: Math.max(right, (left + right) / 2 + hw / 2), moves });
  }
  const out = {};
  arrSideBySide(m, plans, out);
  return arrChanged(m, out);
}

// Snap to grid: everything you placed by hand, and each block and router, lands on the grid. What follows its parent still does.
function arrGrid(m) {
  const [gx, gy] = ARR.GRID, out = {}, roots = new Set();
  for (const u of arrUnits(m)) { roots.add(u.top); for (const b of u.blocks) roots.add(b.key); }
  for (const k of m.nodes.keys()) {
    if (!roots.has(k) && !m.saved[k]) continue;
    const p = m.final.get(k);
    out[k] = [Math.round(p[0] / gx) * gx, Math.round(p[1] / gy) * gy];
  }
  return arrChanged(m, out);
}

// Only what actually moves: a layout that's already straight saves nothing.
function arrChanged(m, out) {
  for (const k of Object.keys(out)) {
    const p = m.final.get(k);
    if (Math.abs(out[k][0] - p[0]) < 1 && Math.abs(out[k][1] - p[1]) < 1) delete out[k];
  }
  return out;
}

function arrArrange(m, kind, ratio) {
  return kind === "compact" ? arrCompact(m, ratio) : kind === "tidy" ? arrTidy(m) : kind === "grid" ? arrGrid(m) : {};
}

// What a selection moves as: each picked node that isn't inside another picked node, with everything that hangs from it (what you
// placed by hand in it included), and the room that takes. Pick a switch and one of its devices and the switch is the one thing.
function arrPicked(m, keys) {
  const kids = arrKids(m), sel = new Set(keys.filter(k => m.nodes.has(k))), out = [];
  for (const k of sel) {
    let p = m.nodes.get(k).parent, inside = false, guard = 0;
    while (p && guard++ < 50) { if (sel.has(p)) { inside = true; break; } p = (m.nodes.get(p) || {}).parent; }
    if (inside) continue;
    const ks = arrTree(m, kids, k), r = arrBox(m, ks);
    out.push({ key: k, keys: ks, r, w: r[2] - r[0], h: r[3] - r[1] });
  }
  return out;
}

// Align the picked things: how is left, hcenter, right, top, vmiddle or bottom, to the edge or middle of all of them together.
function arrAlign(m, keys, how) {
  const bl = arrPicked(m, keys), out = {};
  if (bl.length < 2) return out;
  const x0 = Math.min(...bl.map(b => b.r[0])), x1 = Math.max(...bl.map(b => b.r[2]));
  const y0 = Math.min(...bl.map(b => b.r[1])), y1 = Math.max(...bl.map(b => b.r[3]));
  for (const b of bl) {
    const dx = how === "left" ? x0 - b.r[0] : how === "right" ? x1 - b.r[2] : how === "hcenter" ? (x0 + x1) / 2 - (b.r[0] + b.r[2]) / 2 : 0;
    const dy = how === "top" ? y0 - b.r[1] : how === "bottom" ? y1 - b.r[3] : how === "vmiddle" ? (y0 + y1) / 2 - (b.r[1] + b.r[3]) / 2 : 0;
    arrMove(m, b.keys, dx, dy, out);
  }
  return arrChanged(m, out);
}

// Space the picked things evenly, across (axis h) or down (v): the first and last stay put and the ones between are spread so the
// gaps are all alike, never less than ARR.MIN_GAP, which can push the last one on when they're packed tight.
function arrDistribute(m, keys, axis) {
  const bl = arrPicked(m, keys), out = {};
  if (bl.length < 3) return out;
  const a = axis === "h" ? 0 : 1, size = axis === "h" ? "w" : "h";
  bl.sort((p, q) => p.r[a] - q.r[a] || p.r[1 - a] - q.r[1 - a]);
  const span = Math.max(...bl.map(b => b.r[a + 2])) - bl[0].r[a];
  const gap = Math.max(ARR.MIN_GAP, (span - bl.reduce((s, b) => s + b[size], 0)) / (bl.length - 1));
  let at = bl[0].r[a];
  for (const b of bl) {
    const d = at - b.r[a];
    arrMove(m, b.keys, axis === "h" ? d : 0, axis === "h" ? 0 : d, out);
    at += b[size] + gap;
  }
  return arrChanged(m, out);
}

// While dragging: line a node up with another's row or column if one is within thr (in drawing units), else land it on the grid.
// Returns where it goes and the lines to draw: guideX / guideY are where it lined up, or null. skip: other keys to ignore, such as
// the rest of a selection being dragged along with it.
function arrSnapDrag(m, key, p, thr, skip) {
  const own = new Set(arrTree(m, arrKids(m), key, true));
  if (skip) for (const k of skip) own.add(k);
  let bx = null, by = null, dx = thr, dy = thr;
  for (const [k, q] of m.final) {
    if (own.has(k) || m.nodes.get(k).type === "box") continue;
    if (Math.abs(q[0] - p[0]) < dx) { dx = Math.abs(q[0] - p[0]); bx = q[0]; }
    if (Math.abs(q[1] - p[1]) < dy) { dy = Math.abs(q[1] - p[1]); by = q[1]; }
  }
  const [gx, gy] = ARR.GRID;
  return { p: [bx !== null ? bx : Math.round(p[0] / gx) * gx, by !== null ? by : Math.round(p[1] / gy) * gy], guideX: bx, guideY: by };
}
