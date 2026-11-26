// Named Map layouts: what switching to one changes, and which one the map is on. The code is read from the dashboard
// (js/maplayouts.js) and run. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const src = readFileSync("BAMF/wwwroot/js/maplayouts.js", "utf8");

function take(start) {
  const i = src.indexOf(start);
  assert.notEqual(i, -1, `no "${start}" in the dashboard's scripts`);
  let depth = 0;
  for (let j = src.indexOf("{", i); j < src.length; j++) {
    if (src[j] === "{") depth++;
    else if (src[j] === "}" && --depth === 0) return src.slice(i, j + 1);
  }
  throw new Error(`"${start}" never closes`);
}

const ctx = vm.createContext({});
vm.runInContext(`${take("function layoutPlan(")}\n${take("function layoutIsCurrent(")}
  globalThis.plan = (l, s, k) => { const r = layoutPlan(l, s, k && new Set(k)); return { positions: r.positions, missing: r.missing }; };
  globalThis.current = layoutIsCurrent;`, ctx);
const plan = (...a) => JSON.parse(JSON.stringify(ctx.plan(...a)));

test("switching sets what the layout has and sends back to automatic what it has no place for", () => {
  const r = plan({ "h:1": [10, 20], "s:1": [0, 0] }, { "h:1": [99, 99], "h:2": [5, 5] }, ["h:1", "s:1", "h:2"]);
  assert.deepEqual(r.positions, { "h:1": [10, 20], "s:1": [0, 0], "h:2": null });
  assert.equal(r.missing, 0);
});

test("only what would move is sent, so a map already laid out that way needs nothing", () => {
  const same = { "h:1": [10, 20], "s:1": [0, 0] };
  assert.deepEqual(plan(same, { "h:1": [10.3, 19.8], "s:1": [0, 0] }, ["h:1", "s:1"]).positions, {});
  assert.deepEqual(plan({ "h:1": [10, 20] }, { "h:1": [10, 20] }, ["h:1"]).positions, {});
});

test("a thing the layout has that is no longer on the map is left out and counted", () => {
  const r = plan({ "h:1": [10, 20], "h:7": [1, 1], "s:9": [2, 2] }, {}, ["h:1"]);
  assert.deepEqual(r.positions, { "h:1": [10, 20] });
  assert.equal(r.missing, 2);
});

test("a layout is current when every place matches and nothing else is placed", () => {
  const l = { "h:1": [10, 20], "s:1": [0, 0] };
  assert.equal(ctx.current(l, { "h:1": [10.2, 20], "s:1": [0, 0.4] }), true);
  assert.equal(ctx.current(l, { "h:1": [10, 20] }), false);                       // the map has one fewer placed
  assert.equal(ctx.current(l, { "h:1": [10, 20], "s:1": [0, 0], "h:2": [3, 3] }), false);   // and one more
  assert.equal(ctx.current(l, { "h:1": [50, 20], "s:1": [0, 0] }), false);        // moved since
  assert.equal(ctx.current({}, {}), false);                                       // an empty layout is nothing to be on
});
