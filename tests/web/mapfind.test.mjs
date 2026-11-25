// Find a device on the map: which devices a search matches and where each is drawn, and the view that centres one. The code is read
// from the dashboard (js/map.js) and run. Run from the repo root:
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

const host = (id, name, extra = {}) => ({ id, customName: name, ip: `192.168.30.${id}`, subnet: "192.168.30.0/24", ...extra });
const hosts = [
  host(1, "porch cam"), host(2, "garage cam"), host(3, "old cam", { ignored: true }), host(4, "gone cam", { forgotten: true }),
  host(5, "far cam", { remote: "Cabin" }), host(6, "kitchen speaker"), host(7, "hidden cam"),
];
// Drawn on the map: 1, 2 and 6 (a drawing's nodes are keyed "h:<id>"); the ignored, forgotten, remote and filtered ones are not.
const drawn = new Set([1, 2, 6]);

function page() {
  const ctx = vm.createContext({
    topoModels: { "192.168.30.0/24": { subnet: "192.168.30.0/24", svg: { isConnected: true }, nodes: { has: k => drawn.has(Number(k.slice(2))) } } },
    $: () => ({ querySelector: () => null }),
  });
  vm.runInContext(`
    const hosts = ${JSON.stringify(hosts)};
    function nameOrIp(h) { return h.customName || h.ip; }
    function matchesQuery(h, q) { return nameOrIp(h).toLowerCase().includes(q.toLowerCase()); }
    ${take(map, "function mapFindWhere(")}
    ${take(map, "function mapFindHits(")}
    ${take(map, "function findView(")}
    globalThis.page = { mapFindHits, findView };`, ctx);
  return ctx.page;
}

test("a search lists what matches, the ones on a map first and each by name", () => {
  const hits = page().mapFindHits("cam");
  assert.deepEqual([...hits.map(x => x.host.customName)], ["garage cam", "porch cam", "far cam", "hidden cam", "old cam"]);
});

test("a device that is not drawn says why", () => {
  const why = Object.fromEntries([...page().mapFindHits("cam")].map(x => [x.host.customName, x.why]));
  assert.equal(why["porch cam"], "");
  assert.equal(why["old cam"], "ignored, so not drawn");
  assert.equal(why["far cam"], "on Cabin");
  assert.equal(why["hidden cam"], "not on this map");
});

test("a forgotten device is not found, and an empty search finds nothing", () => {
  const p = page();
  assert.ok(![...p.mapFindHits("cam")].some(x => x.host.customName === "gone cam"));
  assert.equal(p.mapFindHits("").length, 0);
});

test("a device on a drawing is found there", () => {
  const x = [...page().mapFindHits("porch")][0];
  assert.equal(x.at.key, "h:1");
  assert.equal(x.at.subnet, "192.168.30.0/24");
});

test("the view that centres a node keeps the shape of the frame and is never wider than 700", () => {
  const f = page().findView;
  const v = f([0, 0, 1000, 500], [400, 200], { type: "dev", labelW: 108 }, 26);
  assert.equal(v[2], 700);
  assert.equal(v[3], 350);                                  // the same shape as the frame it came from
  assert.equal(v[0] + v[2] / 2, 400 + (12 + 108) / 2);      // the tile and its name are in the middle
  assert.equal(v[1] + v[3] / 2, 200);
});

test("a map already zoomed closer than that stays as close, and a box is centred on its middle", () => {
  const f = page().findView;
  const v = f([0, 0, 300, 200], [100, 100], { type: "box", size: [120, 60] }, 26);
  assert.deepEqual([...v], [100 + 60 - 150, 100 + 30 - 100, 300, 200]);
  assert.equal(f([0, 0, 100, 100], [0, 0], { type: "dev" }, 26)[2], 160);   // the closest zoom there is
});
