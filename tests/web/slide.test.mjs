// The Map's Slide into place: what moves, how it eases, that it lands where the new drawing has things, and that it lets go when
// the map is touched. The code is read from the dashboard (js/map.js) and run. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const map = readFileSync("BAMF/wwwroot/js/map.js", "utf8");

function take(src, start) {
  const i = src.indexOf(start);
  assert.notEqual(i, -1, `no "${start}" in the dashboard's scripts`);
  let depth = 0;
  for (let j = src.indexOf("{", i); j < src.length; j++) {
    if (src[j] === "{") depth++;
    else if (src[j] === "}" && --depth === 0) return src.slice(i, j + 1);
  }
  throw new Error(`"${start}" never closes`);
}

// A page with just the slide's own functions, a fake frame queue, and a stand-in for placing things.
function page(opts = {}) {
  const frames = [];
  const ctx = vm.createContext({
    performance: { now: () => 0 },
    requestAnimationFrame: fn => { frames.push(fn); return frames.length; },
    cancelAnimationFrame: () => { frames.length = 0; },
    window: { matchMedia: () => ({ matches: !!opts.reduced }) },
    topoModels: {},
  });
  vm.runInContext(`let mapSlide = ${opts.on !== false};\nconst SLIDE_MS = 420;\n` + [
    take(map, "function slideMoves("), take(map, "function slideEase("), take(map, "function slideView("),
    take(map, "function topoSlide("), take(map, "function topoSettle("),
  ].join("\n") + `
    function topoPlace(m) { for (const [k, p] of Object.entries(m.override)) m.final.set(k, p); }
    function topoApplyView(m) { m.view = m.target.slice(); m.applied = true; }
    globalThis.page = { slideMoves, slideEase, slideView, topoSlide, topoSettle };`, ctx);
  return { ...ctx.page, ctx, frames };
}

// A drawing: places for things, the view it is in, and an svg that records its listeners and attributes.
function model(finalPlaces, target) {
  const listeners = new Map(), attrs = {};
  const svg = {
    isConnected: true,
    classList: { remove() {}, add() {}, contains: () => false },
    getBoundingClientRect: () => ({}),
    setAttribute: (k, v) => { attrs[k] = v; },
    addEventListener: (ev, fn) => listeners.set(ev, fn),
    removeEventListener: ev => listeners.delete(ev),
  };
  return { subnet: "*", svg, final: new Map(Object.entries(finalPlaces)), override: {}, view: target.slice(), target, listeners, attrs, glide: null };
}

test("only things that moved glide, by more than half a unit, and a thing in only one drawing stays put", () => {
  const { slideMoves } = page();
  const from = new Map([["a", [0, 0]], ["b", [10, 10]], ["gone", [5, 5]]]);
  const to = new Map([["a", [100, 0]], ["b", [10.3, 10]], ["new", [1, 1]]]);
  assert.deepEqual(JSON.parse(JSON.stringify(slideMoves(from, to))), [["a", [0, 0], [100, 0]]]);
});

test("the ease starts quick, lands exactly on 1, and never leaves 0 to 1", () => {
  const { slideEase } = page();
  assert.equal(slideEase(0), 0);
  assert.equal(slideEase(1), 1);
  assert.equal(slideEase(7), 1);
  assert.equal(slideEase(-3), 0);
  assert.ok(slideEase(.25) > .5, "most of the distance is covered in the first quarter");
});

test("the view is carried part of the way, linearly", () => {
  const { slideView } = page();
  assert.deepEqual(JSON.parse(JSON.stringify(slideView([0, 0, 1000, 400], [100, 50, 800, 400], .5))), [50, 25, 900, 400]);
});

test("a slide starts where things were, ends where the drawing has them, and clears what it borrowed", () => {
  const p = page();
  const m = model({ a: [100, 0], b: [10, 10] }, [0, 0, 1000, 400]);
  p.ctx.topoModels["*"] = m;
  assert.equal(p.topoSlide(m, { final: new Map([["a", [0, 0]], ["b", [10, 10]]]), view: [0, 0, 1000, 400] }), true);
  assert.deepEqual(JSON.parse(JSON.stringify(m.override)), { a: [0, 0] });        // the first frame is the old place; b did not move
  assert.equal(typeof m.glide, "function");
  assert.equal(p.frames.length, 1);
  p.ctx.performance.now = () => 420;
  p.frames.shift()(420);                                                          // the clock runs out
  assert.equal(m.glide, null);
  assert.deepEqual(JSON.parse(JSON.stringify(m.override)), {});
  assert.equal(m.applied, true);
  assert.equal(p.frames.length, 0, "no frame is left asking for another");
  assert.equal(m.listeners.size, 0, "it let go of the map's events");
});

test("pressing the map lands a slide at once", () => {
  const p = page();
  const m = model({ a: [100, 0] }, [0, 0, 1000, 400]);
  p.ctx.topoModels["*"] = m;
  p.topoSlide(m, { final: new Map([["a", [0, 0]]]), view: [0, 0, 1000, 400] });
  m.listeners.get("pointerdown")();
  assert.equal(m.glide, null);
  assert.deepEqual(JSON.parse(JSON.stringify(m.override)), {});
  assert.equal(m.applied, true);
  assert.equal(m.listeners.size, 0);
});

test("a drawing that was replaced mid-slide is left alone", () => {
  const p = page();
  const m = model({ a: [100, 0] }, [0, 0, 1000, 400]);
  p.ctx.topoModels["*"] = m;
  p.topoSlide(m, { final: new Map([["a", [0, 0]]]), view: [0, 0, 1000, 400] });
  p.ctx.topoModels["*"] = model({ a: [100, 0] }, [0, 0, 1000, 400]);              // a poll redrew the map
  p.frames.shift()(100);
  assert.equal(m.glide, null);
  assert.equal(p.frames.length, 0);
});

test("nothing to glide when nothing moved or the view stayed", () => {
  const p = page();
  const m = model({ a: [10, 10] }, [0, 0, 1000, 400]);
  assert.equal(p.topoSlide(m, { final: new Map([["a", [10, 10]]]), view: [0, 0, 1000, 400] }), false);
  assert.equal(m.glide, null);
});

test("with the switch off, or reduced motion asked for, the map is not slid", () => {
  for (const opts of [{ on: false }, { reduced: true }]) {
    const p = page(opts);
    const m = model({ a: [100, 0] }, [0, 0, 1000, 400]);
    p.ctx.topoModels["*"] = m;
    p.topoSettle("*", { final: new Map([["a", [0, 0]]]), view: [0, 0, 1000, 400] });
    assert.equal(m.glide, null, JSON.stringify(opts));
    assert.equal(p.frames.length, 0, JSON.stringify(opts));
  }
});
