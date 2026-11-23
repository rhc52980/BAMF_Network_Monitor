// The Map's Arrange tools (Tidy up, Compact, Snap to grid, and snapping while you drag): that they straighten a layout, never
// stack one thing on another, move only what they should, and have nothing left to do the second time. The code is read from
// the dashboard (js/maparrange.js) and run, so this tests what ships. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const src = readFileSync("BAMF/wwwroot/js/maparrange.js", "utf8");
const map = readFileSync("BAMF/wwwroot/js/map.js", "utf8");
const ctx = vm.createContext({ Math, Object, Array, Map, Set, Infinity });
vm.runInContext(src + "\nglobalThis.arr = { ARR, arrCompact, arrTidy, arrGrid, arrSnapDrag, arrUnits, arrArrange };", ctx);
const { ARR, arrCompact, arrTidy, arrGrid, arrSnapDrag, arrUnits, arrArrange } = ctx.arr;

// A map as map.js builds it: a router, and under it `n` switches each with three devices listed beneath. Positions are the ones
// the automatic layout gives: switches in one long row, devices a column in from their switch.
function model(n, { jitter = [], pinned = [], saved = {} } = {}) {
  const nodes = new Map(), final = new Map();
  const add = (key, node, p) => { nodes.set(key, node); final.set(key, p); };
  const colW = 330;
  add("r", { type: "root", parent: null, labelW: 120 }, [n * colW / 2, 0]);
  add("self", { type: "self", parent: "r", labelW: 80 }, [n * colW / 2 + 250, 0]);
  for (let i = 0; i < n; i++) {
    const j = jitter[i] || [0, 0], x = 40 + i * colW + j[0], y = 140 + j[1];
    add("s:" + i, { type: "switch", parent: "r", labelW: 110 }, [x, y]);
    for (let d = 0; d < 3; d++) add(`h:${i}.${d}`, { type: "dev", parent: "s:" + i, labelW: 140 }, [x + 124, y + 70 + d * 46]);
  }
  for (const k of pinned) saved[k] = final.get(k).slice();
  return { nodes, final, saved, override: {} };
}

// Where each block sits once `out` is applied: a node's saved place, else wherever it already is plus its parent's move.
function applied(m, out) {
  const at = new Map();
  const get = k => {
    if (at.has(k)) return at.get(k);
    const n = m.nodes.get(k), now = m.final.get(k);
    let p;
    if (out[k]) p = out[k];
    else if (m.saved[k]) p = m.saved[k];
    else if (n.parent) { const pp = get(n.parent), pn = m.final.get(n.parent); p = [now[0] + pp[0] - pn[0], now[1] + pp[1] - pn[1]]; }
    else p = now;
    at.set(k, p);
    return p;
  };
  for (const k of m.nodes.keys()) get(k);
  return at;
}
function blocksOf(m, out) {
  const at = applied(m, out);
  return [...m.nodes.keys()].filter(k => k.startsWith("s:")).map(k => {
    const keys = [k, ...[...m.nodes].filter(([, n]) => n.parent === k).map(([x]) => x)];
    const xs = keys.map(x => at.get(x)), r = [Math.min(...xs.map(p => p[0])) - 20, Math.min(...xs.map(p => p[1])) - 46, Math.max(...xs.map(p => p[0])) + 160, Math.max(...xs.map(p => p[1])) + 40];
    return { key: k, at: at.get(k), r };
  });
}
// Objects built inside the vm have its prototypes; compare by value.
const plain = x => JSON.parse(JSON.stringify(x));
const overlap = (a, b) => a.r[0] < b.r[2] && b.r[0] < a.r[2] && a.r[1] < b.r[3] && b.r[1] < a.r[3];

test("Compact turns one long strip into rows near the shape asked for", () => {
  const m = model(6);
  const out = arrCompact(m, 1.5);
  const bl = blocksOf(m, out);
  const w = Math.max(...bl.map(b => b.r[2])) - Math.min(...bl.map(b => b.r[0])), h = Math.max(...bl.map(b => b.r[3])) + 0;
  assert.ok(w < 6 * 330 * 0.7, "much narrower than the strip it was: " + w);
  assert.ok(new Set(bl.map(b => Math.round(b.at[1]))).size > 1, "more than one row");
  for (let i = 0; i < bl.length; i++) for (let j = i + 1; j < bl.length; j++) assert.ok(!overlap(bl[i], bl[j]), `${bl[i].key} and ${bl[j].key} overlap`);
  assert.ok(h > 0);
});

test("Compact keeps the order, and a wider ask gives wider rows", () => {
  const narrow = blocksOf(model(6), arrCompact(model(6), 1)), wide = blocksOf(model(6), arrCompact(model(6), 4));
  const rows = bl => new Set(bl.map(b => Math.round(b.at[1]))).size;
  assert.ok(rows(wide) < rows(narrow), "a wide frame wants fewer rows");
  // Reading left to right, then down, it's still 0, 1, 2…
  const order = narrow.slice().sort((a, b) => Math.round(a.at[1]) - Math.round(b.at[1]) || a.at[0] - b.at[0]).map(b => b.key);
  assert.deepEqual(plain(order), ["s:0", "s:1", "s:2", "s:3", "s:4", "s:5"]);
});

test("Arranging saves only the blocks' own nodes: what follows its parent is left to follow", () => {
  const m = model(4);
  const out = arrCompact(m, 1.5);
  assert.ok(Object.keys(out).every(k => k === "r" || k.startsWith("s:")), Object.keys(out).join());
  assert.ok(!("h:0.0" in out) && !("self" in out));
});

test("Something you placed by hand inside a block travels with the block", () => {
  const m = model(4, { pinned: ["h:2.1"] });
  const before = m.final.get("h:2.1").slice(), sw = m.final.get("s:2").slice();
  const out = arrCompact(m, 1.5);
  assert.ok(out["h:2.1"], "the pinned device is saved at its new place");
  const dx = out["s:2"][0] - sw[0], dy = out["s:2"][1] - sw[1];
  assert.deepEqual(plain(out["h:2.1"]), [Math.round(before[0] + dx), Math.round(before[1] + dy)]);
});

test("Tidy up makes rows level, spaces them evenly and clears overlaps", () => {
  const jitter = [[23, -31], [-37, 29], [41, 17], [-19, -26], [33, 38], [-28, 12]];
  // First make two rows of three, as someone dragging roughly would, then knock them about.
  const m = model(6, { jitter });
  for (let i = 3; i < 6; i++) for (const [k, n] of m.nodes) if (k === "s:" + i || n.parent === "s:" + i) { const p = m.final.get(k); m.final.set(k, [p[0] - 3 * 330, p[1] + 330]); }
  const out = arrTidy(m);
  const bl = blocksOf(m, out);
  for (const row of [bl.slice(0, 3), bl.slice(3)]) {
    assert.equal(new Set(row.map(b => Math.round(b.at[1]))).size, 1, "a row is level");
    const xs = row.slice().sort((a, b) => a.at[0] - b.at[0]);
    const gaps = xs.slice(1).map((b, i) => b.r[0] - xs[i].r[2]);
    assert.ok(gaps.every(g => g >= ARR.GAP_X - 2), "gaps at least the least: " + gaps.join());
    assert.ok(Math.max(...gaps) - Math.min(...gaps) < 2, "and even: " + gaps.join());
  }
  for (let i = 0; i < bl.length; i++) for (let j = i + 1; j < bl.length; j++) assert.ok(!overlap(bl[i], bl[j]), "no overlap");
  assert.equal(Math.round(bl[3].r[0]), Math.round(bl[0].r[0]), "rows that start about together start together");
});

// The map as it is once `out` has been saved.
function commit(m, out) {
  const at = applied(m, out), saved = { ...m.saved, ...out };
  return { nodes: m.nodes, final: new Map([...at].map(([k, p]) => [k, p.slice()])), saved, override: {} };
}

test("Tidy up has nothing left to do the second time", () => {
  const m = model(5, { jitter: [[10, 8], [-12, 5], [6, -9], [-4, 11], [9, -6]] });
  const first = arrTidy(m);
  assert.ok(Object.keys(first).length > 0, "a rough row gets straightened");
  assert.deepEqual(plain(Object.keys(arrTidy(commit(m, first)))), []);
});

test("Snap to grid puts the blocks and what you placed on the grid, and again does nothing", () => {
  const m = model(3, { jitter: [[7, 5], [-9, 11], [13, -6]], pinned: ["h:1.0"] });
  const out = arrGrid(m);
  for (const k of ["s:0", "s:1", "s:2", "h:1.0"]) {
    assert.equal(out[k][0] % ARR.GRID[0], 0, k + " x");
    assert.equal(out[k][1] % ARR.GRID[1], 0, k + " y");
  }
  assert.ok(!("h:0.0" in out), "what follows its parent isn't pinned to the grid");
  // Apply it, then ask again.
  for (const [k, p] of Object.entries(out)) { m.final.set(k, p); }
  assert.deepEqual(plain(arrGrid(m)), {});
});

test("Dragging lines a node up with a neighbour within reach, else lands on the grid", () => {
  const m = model(4);
  m.final.set("s:1", [700, 200]);
  // Near s:2's x and s:0's y (140): lines up with both.
  const near = arrSnapDrag(m, "s:1", [m.final.get("s:2")[0] + 6, 147], 12);
  assert.equal(near.guideX, m.final.get("s:2")[0]);
  assert.equal(near.guideY, 140);
  assert.deepEqual(plain(near.p), [m.final.get("s:2")[0], 140]);
  // Nothing in reach: the grid.
  const free = arrSnapDrag(m, "s:1", [1503, 905], 12);
  assert.equal(free.guideX, null);
  assert.equal(free.guideY, null);
  assert.deepEqual(plain(free.p), [Math.round(1503 / ARR.GRID[0]) * ARR.GRID[0], Math.round(905 / ARR.GRID[1]) * ARR.GRID[1]]);
  // The dragged node's own devices go with it, so it doesn't snap to them.
  const own = arrSnapDrag(m, "s:1", [m.final.get("h:1.0")[0] + 3, 300], 12);
  assert.equal(own.guideX, null);
});

test("A box lines up by its connection, 24 under its top edge, like the automatic layout", () => {
  const m = model(2);
  m.nodes.set("box", { type: "box", parent: "r", size: [260, 300] });
  m.final.set("box", [700, 116]);
  const u = arrUnits(m)[0];
  const box = u.blocks.find(b => b.key === "box");
  assert.equal(box.ay, 140);
  const sw = u.blocks.find(b => b.key === "s:0");
  assert.equal(sw.ay, 140);
  assert.deepEqual(plain(Object.keys(arrArrange(m, "nonsense"))), []);
});

test("A line to a block on a lower row runs down a trunk at the left, not through the rows above", () => {
  const code = map.slice(map.indexOf("function topoPath("), map.indexOf("// Blocks under one router that sit on more than one row"));
  const c = vm.createContext({ TOPO: { T: 16 } });
  vm.runInContext(code + "\nglobalThis.topoPath = topoPath;", c);
  const flat = c.topoPath("down", [500, 0], [300, 144]);
  const trunked = c.topoPath("down", [500, 0], [300, 450], undefined, 40);
  assert.ok(!/H40\b/.test(flat), "the first row is drawn as ever");
  assert.match(trunked, /H40\.0 V/, "a lower row goes round by the trunk");
  assert.match(trunked, /V\d+\.\d H300\.0 V434\.0$/, "and arrives from above");
});
