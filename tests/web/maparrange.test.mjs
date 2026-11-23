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
vm.runInContext(src + "\nglobalThis.arr = { ARR, arrCompact, arrTidy, arrGrid, arrSnapDrag, arrUnits, arrArrange, arrPicked, arrAlign, arrDistribute };", ctx);
const { ARR, arrCompact, arrTidy, arrGrid, arrSnapDrag, arrUnits, arrArrange, arrPicked, arrAlign, arrDistribute } = ctx.arr;

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

// ---- Selecting: Align and Distribute ----

test("Picking a switch and one of its devices is one thing: the switch, with the device along", () => {
  const m = model(3);
  const bl = arrPicked(m, ["s:0", "h:0.1", "s:2"]);
  assert.deepEqual(plain(bl.map(b => b.key)), ["s:0", "s:2"]);
  assert.ok(bl[0].keys.includes("h:0.1") && bl[0].keys.includes("h:0.2"), "a switch takes all its devices");
  assert.deepEqual(plain(arrPicked(m, ["nothing", "h:1.0"]).map(b => b.key)), ["h:1.0"]);
});

test("Align left, right and centre put the picked blocks on one edge or middle, and move nothing else", () => {
  const m = model(4, { jitter: [[0, 0], [30, 10], [-40, 20], [15, -5]] });
  const keys = ["s:0", "s:1", "s:2"];
  const bl = out => blocksOf(m, out).filter(b => keys.includes(b.key));
  const left = bl(arrAlign(m, keys, "left")), right = bl(arrAlign(m, keys, "right")), mid = bl(arrAlign(m, keys, "hcenter"));
  assert.equal(new Set(left.map(b => Math.round(b.r[0]))).size, 1, "left edges together");
  assert.equal(new Set(right.map(b => Math.round(b.r[2]))).size, 1, "right edges together");
  assert.equal(new Set(mid.map(b => Math.round((b.r[0] + b.r[2]) / 2))).size, 1, "centres together");
  assert.ok(!Object.keys(arrAlign(m, keys, "left")).some(k => k === "s:3" || k === "r"), "what wasn't picked stays");
});

test("Align top, middle and bottom work down the page the same way, and only the picked block's own node is saved", () => {
  const m = model(3, { jitter: [[0, 0], [0, 60], [0, -30]] });
  const keys = ["s:0", "s:1", "s:2"];
  const top = blocksOf(m, arrAlign(m, keys, "top")), bottom = blocksOf(m, arrAlign(m, keys, "bottom"));
  assert.equal(new Set(top.map(b => Math.round(b.r[1]))).size, 1);
  assert.equal(new Set(bottom.map(b => Math.round(b.r[3]))).size, 1);
  assert.equal(new Set(blocksOf(m, arrAlign(m, keys, "vmiddle")).map(b => Math.round((b.r[1] + b.r[3]) / 2))).size, 1);
  assert.ok(Object.keys(arrAlign(m, keys, "top")).every(k => k.startsWith("s:")), "devices just follow");
});

test("Aligning is a no-op with one thing, and again once done", () => {
  const m = model(3, { jitter: [[0, 0], [20, 30], [-10, 5]] });
  assert.deepEqual(plain(arrAlign(m, ["s:1"], "left")), {});
  const out = arrAlign(m, ["s:0", "s:1", "s:2"], "left");
  assert.ok(Object.keys(out).length > 0);
  assert.deepEqual(plain(arrAlign(commit(m, out), ["s:0", "s:1", "s:2"], "left")), {});
});

test("Distribute spaces three or more with equal gaps and leaves the two ends where they are", () => {
  const m = model(4, { jitter: [[0, 0], [-120, 0], [50, 0], [250, 0]] });
  const keys = ["s:0", "s:1", "s:2", "s:3"];
  const before = blocksOf(m, {}), out = arrDistribute(m, keys, "h");
  const bl = blocksOf(m, out).sort((a, b) => a.r[0] - b.r[0]);
  const gaps = bl.slice(1).map((b, i) => b.r[0] - bl[i].r[2]);
  assert.ok(Math.max(...gaps) - Math.min(...gaps) < 2, "even: " + gaps.join());
  const first = before.slice().sort((a, b) => a.r[0] - b.r[0])[0], last = before.slice().sort((a, b) => a.r[2] - b.r[2]).pop();
  assert.equal(Math.round(bl[0].r[0]), Math.round(first.r[0]), "the left end stays");
  assert.equal(Math.round(bl[bl.length - 1].r[2]), Math.round(last.r[2]), "and the right");
  assert.deepEqual(plain(arrDistribute(m, ["s:0", "s:1"], "h")), {}, "two can't be spread");
});

test("Distribute down, and never closer than the least gap", () => {
  const m = model(4);
  // Four blocks in a column, the middle two nearly touching.
  const ys = [0, 300, 330, 900];
  for (let i = 0; i < 4; i++) for (const [k, n] of m.nodes) if (k === "s:" + i || n.parent === "s:" + i) { const p = m.final.get(k); m.final.set(k, [40, p[1] - 140 + ys[i]]); }
  const bl = blocksOf(m, arrDistribute(m, ["s:0", "s:1", "s:2", "s:3"], "v")).sort((a, b) => a.r[1] - b.r[1]);
  const gaps = bl.slice(1).map((b, i) => b.r[1] - bl[i].r[3]);
  assert.ok(gaps.every(g => g >= ARR.MIN_GAP - 1), gaps.join());
  assert.ok(Math.max(...gaps) - Math.min(...gaps) < 2, "even: " + gaps.join());
});

test("Dragging a picked group ignores the group when snapping to neighbours", () => {
  const m = model(4);
  const skip = new Set(["s:2", "h:2.0", "h:2.1", "h:2.2"]);
  const withIt = arrSnapDrag(m, "s:1", [m.final.get("s:2")[0] + 4, 400], 12);
  const without = arrSnapDrag(m, "s:1", [m.final.get("s:2")[0] + 4, 400], 12, skip);
  assert.equal(withIt.guideX, m.final.get("s:2")[0]);
  assert.equal(without.guideX, null);
});
