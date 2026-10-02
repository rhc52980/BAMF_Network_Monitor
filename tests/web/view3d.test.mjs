// The 3D tab's data side: which model a device gets, where it's placed, what the scene is made of. These are
// plain functions in view3d/data.mjs, so they're imported as they ship. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { MODELS, modelFor, stateOf, buildScene, ringsFor, slotsFor, placeNetworks, describeScene } from "../../BAMF/wwwroot/view3d/data.mjs";

// Every kind the Map can give a device (deviceKind in js/map.js).
const MAP_KINDS = ["router", "vpn", "switch", "ap", "camera", "printer", "tv", "speaker", "phone", "tablet", "laptop", "desktop",
  "server", "iot", "net", "device", "vm", "nas", "game", "light", "plug", "vswitch"];

test("every kind the Map can assign has a model, and every model has a file", () => {
  for (const k of MAP_KINDS) assert.ok(Object.hasOwn(MODELS, modelFor(k)), `${k} has no model`);
  for (const k of Object.keys(MODELS)) assert.ok(existsSync(`BAMF/wwwroot/view3d/models/${k}.glb`), `${k}.glb is missing`);
});

test("a Raspberry Pi is told apart from other servers; unknown kinds fall back to the generic model", () => {
  assert.equal(modelFor("server", "Raspberry Pi Trading Ltd"), "pi");
  assert.equal(modelFor("server", "Dell"), "server");
  assert.equal(modelFor("vswitch"), "switch");
  assert.equal(modelFor("net"), "router");
  assert.equal(modelFor("toaster"), "device");
  assert.equal(modelFor("constructor"), "device");
});

test("a device's state: unusual wins, then offline, then not yet approved", () => {
  assert.equal(stateOf({ online: false, known: true }, true), "odd");
  assert.equal(stateOf({ online: false, known: true }, false), "off");
  assert.equal(stateOf({ online: true, known: false }, false), "unk");
  assert.equal(stateOf({ online: true, known: true }, false), "on");
});

const host = (id, o = {}) => ({ id, ip: `10.0.0.${id}`, mac: "aa", subnet: "10.0.0.0/24", online: true, known: true, ...o });
const scene = (hosts, extra = {}) => buildScene({ hosts, kindOf: h => h.kind || "device", nameOf: h => h.name || `h${h.id}`, ...extra });

test("ignored and forgotten devices aren't drawn; networks come out in address order", () => {
  const s = scene([host(1), host(2, { ignored: true }), host(3, { forgotten: true }),
    host(4, { subnet: "10.0.10.0/24" }), host(5, { subnet: "10.0.2.0/24" })]);
  assert.deepEqual(s.nets.map(n => n.cidr), ["10.0.0.0/24", "10.0.2.0/24", "10.0.10.0/24"]);
  assert.equal(s.devices.length, 3);
});

test("the gateway is the device at the gateway's address, else the first router", () => {
  const hosts = [host(1, { kind: "nas" }), host(2, { kind: "router" }), host(3, { kind: "tv" })];
  assert.equal(scene(hosts, { gatewayIp: () => "10.0.0.3" }).nets[0].gateway, "3");
  assert.equal(scene(hosts).nets[0].gateway, "2");
  assert.equal(scene([host(1, { kind: "nas" })]).nets[0].gateway, null);
});

test("only an open unusual finding marks a device, and its words reach the card", () => {
  const unusual = [{ hostId: 1, open: true, title: "Off a long time", detail: "Never this long" }, { hostId: 2, open: false, title: "old" }];
  const s = scene([host(1), host(2)], { unusual });
  assert.equal(s.devices[0].state, "odd");
  assert.equal(s.devices[0].odd.title, "Off a long time");
  assert.equal(s.devices[1].odd, null);
  assert.match(describeScene(s), /1 doing something unusual/);
});

test("traffic and an offline device: no traffic when it's off, always within 1 to 9 when on", () => {
  const s = scene([host(1, { online: false }), host(2), host(3)], { rateOf: h => (h.id === 3 ? 99 : -4) });
  assert.deepEqual(s.devices.map(d => d.rate), [0, 1, 9]);
});

test("rings: nobody overlaps, and the platform is wide enough for the outer ring", () => {
  for (const n of [0, 1, 5, 18, 60, 250]) {
    const rings = ringsFor(n);
    assert.equal(rings.reduce((a, r) => a + r.n, 0), n);
    for (const r of rings) assert.ok((2 * Math.PI * r.r) / Math.max(r.n, 1) >= 2.4 - 1e-9 || r.n <= 5, `ring ${r.r} is crowded`);
  }
  const devices = Array.from({ length: 40 }, (_, i) => ({ key: String(i), model: "device", ip: `10.0.0.${i}`, gw: i === 0 }));
  const { slots, radius } = slotsFor(devices);
  assert.equal(slots.size, 39);
  for (const { x, z } of slots.values()) assert.ok(Math.hypot(x, z) < radius);
  const pts = [...slots.values()];
  for (let i = 0; i < pts.length; i++) for (let j = i + 1; j < pts.length; j++) assert.ok(Math.hypot(pts[i].x - pts[j].x, pts[i].z - pts[j].z) > 1.5);
});

test("platforms side by side don't touch; stacked they're floors", () => {
  for (const radii of [[8], [8, 5], [9, 7, 6], [9, 8, 7, 6, 5]]) {
    const { centers } = placeNetworks(radii, "side");
    for (let i = 0; i < radii.length; i++) for (let j = i + 1; j < radii.length; j++) {
      const gap = Math.hypot(centers[i].x - centers[j].x, centers[i].z - centers[j].z) - radii[i] - radii[j];
      assert.ok(gap > 0.5, `platforms ${i} and ${j} are ${gap.toFixed(2)} apart`);
    }
  }
  const { centers, height } = placeNetworks([5, 9, 7], "stack");
  assert.equal(centers[1].y, 0);                   // the biggest at the bottom
  assert.ok(centers.every(c => c.x === 0 && c.z === 0));
  assert.equal(height, 13);
  assert.equal(placeNetworks([], "side").centers.length, 0);
});
