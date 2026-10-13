// The 3D tab's finding, triage, cabling and placing, on the data side: plain functions in engine3d/data.mjs.
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { buildScene, matchesFilter, searchRank, problemsOf, behindOf, rateScale, formatRate, placementFromPoint, placeInHouse, FILTERS } from "../../BAMF/wwwroot/engine3d/data.mjs";

const host = (id, more = {}) => ({ id, ip: `10.0.0.${id}`, mac: `AA:00:00:00:00:${String(id).padStart(2, "0")}`, subnet: "10.0.0.0/24", online: true, known: true, latencyMs: 5, vendor: "Acme", ...more });
const scene = (hosts, more = {}) => buildScene({ hosts, kindOf: () => "server", nameOf: h => h.name || "d" + h.id, ...more });

test("a device's tags, traffic, web page and what it is plugged into come through, and a loop is cut", () => {
  const hosts = [host(1, { name: "switch" }), host(2, { name: "cam" }), host(3, { name: "tv" }), host(4, { name: "other-net", subnet: "10.9.0.0/24" })];
  const parents = { 2: 1, 3: 2, 1: 3, 4: 1 };            // a ring of three, and one on another network
  const s = scene(hosts, {
    tagsOf: h => h.id === 2 ? ["kids", "IoT"] : [], parentOf: h => parents[h.id] ?? null,
    trafficOf: h => h.id === 2 ? { rx: 125000, tx: 5000 } : null, webOf: h => h.id === 3 ? { url: "http://10.0.0.3/" } : null,
  });
  const by = Object.fromEntries(s.devices.map(d => [d.id, d]));
  assert.deepEqual(by[2].tags, ["kids", "IoT"]);
  assert.equal(by[2].rx, 125000);
  assert.equal(by[3].web.url, "http://10.0.0.3/");
  assert.equal(by[2].parentId, 1);
  // the ring: every one of the three is part of a loop, so none is made a parent of another
  assert.equal(by[1].parent, null);
  assert.equal(by[2].parent, null);
  assert.equal(by[3].parent, null);
  assert.equal(by[4].parent, null);                       // its switch is on another network
  // without the loop, the chain is kept
  const ok = scene(hosts.slice(0, 3), { parentOf: h => ({ 2: 1, 3: 2 })[h.id] ?? null });
  const k = Object.fromEntries(ok.devices.map(d => [d.id, d]));
  assert.equal(k[2].parent, k[1].key);
  assert.equal(k[3].parent, k[2].key);
  assert.equal(k[1].parent, null);
});

test("the search words must all be there, the chips are any-of, and the tag must match", () => {
  const d = { name: "living-room-tv", ip: "10.0.0.7", mac: "AA:BB", vendor: "Samsung", kind: "tv", model: "tv", tags: ["Kids"], state: "unk", odd: null, services: [], watching: false, strain: 0 };
  assert.ok(matchesFilter(d, null));
  assert.ok(matchesFilter(d, { q: "" }));
  assert.ok(matchesFilter(d, { q: "living tv" }));          // words, in any order
  assert.ok(matchesFilter(d, { q: "SAMSUNG" }));
  assert.ok(matchesFilter(d, { q: "10.0.0.7" }));
  assert.ok(matchesFilter(d, { q: "kids" }));               // a tag is in the words too
  assert.ok(!matchesFilter(d, { q: "living kitchen" }));
  assert.ok(matchesFilter(d, { tag: "kids" }));
  assert.ok(!matchesFilter(d, { tag: "guests" }));
  assert.ok(matchesFilter(d, { on: ["unk"] }));
  assert.ok(!matchesFilter(d, { on: ["off"] }));
  assert.ok(matchesFilter(d, { on: ["off", "unk"] }));      // either
  assert.ok(!matchesFilter(d, { on: ["unk"], q: "kitchen" }));
  assert.deepEqual(FILTERS.map(f => f.key), ["off", "unk", "odd", "svc", "watched", "slow"]);
});

test("a name is worth more than an address, which is worth more than anything else", () => {
  const d = { name: "Garage Cam", ip: "10.0.0.5", vendor: "Reolink", kind: "camera", tags: ["outdoor"] };
  assert.equal(searchRank(d, ""), 0);
  assert.equal(searchRank(d, "garage cam"), 100);
  assert.equal(searchRank(d, "10.0.0.5"), 95);
  assert.equal(searchRank(d, "gar"), 80);
  assert.equal(searchRank(d, "cam"), 60);
  assert.equal(searchRank(d, "10.0.0"), 55);
  assert.equal(searchRank(d, "reolink"), 30);
  assert.equal(searchRank(d, "toaster"), 0);
});

test("what needs looking at is listed most urgent first, with a device saying each thing once", () => {
  const dev = (key, more) => ({ key, name: key, ip: "10.0.0." + key.length, online: true, state: "on", services: [], odd: null, strain: 0, ms: 5, watching: false, ...more });
  const list = problemsOf([
    dev("quiet"),
    dev("newbie", { state: "unk" }),
    dev("weird", { odd: { title: "weird is off at night", detail: "x" }, state: "odd" }),
    dev("nas", { services: [{ name: "Plex", state: "down" }, { name: "SSH", state: "up" }] }),
    dev("camera", { watching: true, state: "off", online: false }),
    dev("laptop", { strain: 0.9, ms: 420 }),
    dev("pi"),
  ], new Set(["pi"]));
  assert.deepEqual(list.map(p => [p.key, p.kind]), [["camera", "down"], ["nas", "service"], ["pi", "scan"], ["weird", "odd"], ["newbie", "new"], ["laptop", "slow"]]);
  assert.equal(list[1].title, "Plex on nas is down");
  assert.equal(list[5].detail, "420 ms.");
  assert.deepEqual(problemsOf([dev("a"), dev("b")]), []);
  // an unwatched device that is off is not a problem; a watched one that is on is not either
  assert.deepEqual(problemsOf([dev("x", { state: "off", online: false }), dev("y", { watching: true })]), []);
});

test("how many are behind a switch counts everything below it, and how many of those are off", () => {
  const d = (key, parent, state = "on") => ({ key, parent, state });
  const m = behindOf([d("gw", null), d("sw1", "gw"), d("sw2", "sw1"), d("a", "sw1", "off"), d("b", "sw2"), d("c", "sw2", "off"), d("solo", null)]);
  assert.deepEqual([...m.keys()].sort(), ["gw", "sw1", "sw2"]);
  assert.equal(m.get("gw").count, 5);
  assert.equal(m.get("sw1").count, 4);
  assert.equal(m.get("sw1").offline, 2);
  assert.deepEqual(m.get("sw2").keys.sort(), ["b", "c"]);
});

test("a measured rate is drawn on a log scale, and read in bits", () => {
  assert.equal(rateScale(0), 0);
  assert.equal(rateScale(null), 0);
  assert.equal(rateScale(1000), 1);
  assert.ok(rateScale(100_000) > rateScale(10_000));
  assert.equal(rateScale(1_000_000), 9);
  assert.equal(rateScale(900_000_000), 9);
  assert.equal(formatRate(0), "0 bps");
  assert.equal(formatRate(125), "1 kbps");
  assert.equal(formatRate(42_500), "340 kbps");
  assert.equal(formatRate(150_000), "1.2 Mbps");
  assert.equal(formatRate(12_500_000), "100 Mbps");
  assert.equal(formatRate(1_250_000_000), "10.0 Gbps");
});

test("a point on the floor goes back to where it was placed, and stays inside the plan", () => {
  const plan = { width: 1200, height: 800, unit: "ft", step: 40, perStep: 1 };
  for (const [fx, fy] of [[0.5, 0.5], [0.1, 0.9], [0.77, 0.23]]) {
    const w = placeInHouse({ x: fx, y: fy }, plan, 0);
    const back = placementFromPoint(w.x, w.z, plan);
    assert.ok(Math.abs(back.x - fx) < 1e-9 && Math.abs(back.y - fy) < 1e-9, `${fx},${fy}`);
  }
  const out = placementFromPoint(9999, -9999, plan);
  assert.deepEqual(out, { x: 1, y: 0 });
});
