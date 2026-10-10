// What the 3D tab shows beyond the devices, on the data side: strain, a device's services, satellites for where devices talk,
// the day replayed from its events, and a drawn plan stood up as a house. Plain functions in engine3d/data.mjs.
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { buildScene, strainOf, worstService, satelliteSlots, stateAt, houseOf, planScale, placeInHouse, WALL_HEIGHT, FLOOR_GAP } from "../../BAMF/wwwroot/engine3d/data.mjs";

test("strain is nothing under 40 ms and everything at 400, easing between", () => {
  assert.equal(strainOf(null), 0);
  assert.equal(strainOf(12), 0);
  assert.equal(strainOf(40), 0);
  assert.ok(Math.abs(strainOf(400) - 1) < 1e-9);
  assert.equal(strainOf(5000), 1);
  assert.ok(strainOf(100) > strainOf(60) && strainOf(100) < 0.5);
});

test("a device's services come to their worst state, and nothing watched is null", () => {
  assert.equal(worstService([]), null);
  assert.equal(worstService(undefined), null);
  assert.equal(worstService([{ state: "up" }, { state: "up" }]), "up");
  assert.equal(worstService([{ state: "up" }, { state: "slow" }]), "slow");
  assert.equal(worstService([{ state: "slow" }, { state: "down" }, { state: "up" }]), "down");
  assert.equal(worstService([{ state: "off" }, { state: "up" }]), "off");
});

test("the scene carries each device's services and strain", () => {
  const hosts = [
    { id: 1, ip: "10.0.0.1", mac: "A", subnet: "10.0.0.0/24", online: true, known: true, latencyMs: 20 },
    { id: 2, ip: "10.0.0.2", mac: "B", subnet: "10.0.0.0/24", online: true, known: true, latencyMs: 400 },
    { id: 3, ip: "10.0.0.3", mac: "C", subnet: "10.0.0.0/24", online: false, known: true, latencyMs: 400 },
  ];
  const scene = buildScene({
    hosts, kindOf: () => "server", nameOf: h => "d" + h.id,
    servicesOf: h => h.id === 2 ? [{ name: "Plex", state: "down", uptime: 97.4, ms: null }] : [],
  });
  const by = Object.fromEntries(scene.devices.map(d => [d.id, d]));
  assert.equal(by[1].strain, 0);
  assert.ok(by[2].strain > 0.99);
  assert.equal(by[3].strain, 0);                                         // off: no answer time to be strained
  assert.deepEqual(by[2].services, [{ name: "Plex", state: "down", uptime: 97.4, ms: null }]);
  assert.deepEqual(by[1].services, []);
});

test("satellites: the more recent sit closer in, hot ones are the last ten minutes, and there are at most as many as asked for", () => {
  const now = Date.parse("2026-10-08T12:00:00Z");
  const dests = [
    { net: "1.1.1.0/24", lastSeen: "2026-10-08T11:58:00Z", devices: [1], isNew: true },
    { net: "2.2.2.0/24", lastSeen: "2026-10-08T09:00:00Z", devices: [1, 2] },
    { net: "3.3.3.0/24", lastSeen: "2026-10-07T12:00:00Z", devices: [3] },
  ];
  const s = satelliteSlots(dests, now);
  assert.equal(s.length, 3);
  assert.equal(s[0].hot, true);
  assert.equal(s[0].fresh, true);
  assert.equal(s[1].hot, false);
  assert.ok(s[0].radius < s[2].radius + 0.9);
  assert.ok(s.every(x => x.radius >= 3.1 && x.radius <= 6.9));
  assert.equal(satelliteSlots(dests, now, 2).length, 2);
  assert.deepEqual(satelliteSlots([], now), []);
});

test("the day replayed: who existed, who was online, the internet and what was unusual, at any moment", () => {
  const tl = {
    from: "2026-10-07T12:00:00Z", to: "2026-10-08T12:00:00Z",
    hosts: [
      { id: 1, first: "2026-10-07T12:00:00Z", online0: true },
      { id: 2, first: "2026-10-07T12:00:00Z", online0: false },
      { id: 3, first: "2026-10-07T18:00:00Z", online0: true },        // arrived at six in the evening
    ],
    events: [
      { h: 1, type: "offline", at: "2026-10-07T14:00:00Z" },
      { h: 2, type: "online", at: "2026-10-07T15:00:00Z" },
      { h: 1, type: "online", at: "2026-10-07T16:00:00Z" },
    ],
    outages: [{ start: "2026-10-07T20:00:00Z", end: "2026-10-07T20:10:00Z" }],
    unusual: [{ hostId: 2, at: "2026-10-07T21:00:00Z", resolvedAt: "2026-10-07T22:00:00Z" }],
  };
  const at = iso => stateAt(tl, Date.parse(iso));
  let s = at("2026-10-07T12:30:00Z");
  assert.deepEqual([...s.present].sort(), [1, 2]);                      // 3 hadn't arrived
  assert.equal(s.online.get(1), true);
  assert.equal(s.online.get(2), false);
  s = at("2026-10-07T14:30:00Z");
  assert.equal(s.online.get(1), false);
  s = at("2026-10-07T15:30:00Z");
  assert.equal(s.online.get(2), true);
  s = at("2026-10-07T17:00:00Z");
  assert.equal(s.online.get(1), true);
  s = at("2026-10-07T18:30:00Z");
  assert.ok(s.present.has(3));
  assert.equal(s.internetDown, false);
  assert.equal(at("2026-10-07T20:05:00Z").internetDown, true);
  assert.equal(at("2026-10-07T20:30:00Z").internetDown, false);
  assert.deepEqual([...at("2026-10-07T21:30:00Z").unusualOpen], [2]);
  assert.deepEqual([...at("2026-10-07T23:00:00Z").unusualOpen], []);
});

test("a drawn plan is scaled from its own units, and a placement is a fraction of it", () => {
  // 40 px to a foot-step of 1 ft: a 12 ft wall is 480 px, six scene units at two feet a unit.
  const plan = { v: 1, width: 1200, height: 800, unit: "ft", step: 40, perStep: 1, items: [
    { k: "wall", a: [100, 100], b: [580, 100] }, { k: "wall", a: [580, 100], b: [580, 500] },
    { k: "door", a: [200, 100], b: [240, 100] }, { k: "window", a: [580, 200], b: [580, 280] },
    { k: "label", p: [300, 300], t: "Kitchen" },
  ] };
  assert.ok(Math.abs(planScale(plan) - 1 / 80) < 1e-12);               // 1 ft per 40 px, two feet a unit
  const metres = { ...plan, unit: "m", perStep: 1 };
  assert.ok(Math.abs(planScale(metres) - 3.28084 / 80) < 1e-9);

  const h = houseOf(plan);
  assert.equal(h.walls.length, 2);
  assert.equal(h.doors.length, 1);
  assert.equal(h.windows.length, 1);
  assert.deepEqual(h.labels.map(l => l.t), ["Kitchen"]);
  const len = Math.hypot(h.walls[0].b[0] - h.walls[0].a[0], h.walls[0].b[1] - h.walls[0].a[1]);
  assert.ok(Math.abs(len - 6) < 1e-9);                                  // 12 ft, six units
  assert.ok(Math.abs(h.size[0] - 6) < 1e-9 && Math.abs(h.size[1] - 5) < 1e-9);

  // The middle of the plan is the origin, whichever way it's measured.
  const mid = placeInHouse({ x: 0.5, y: 0.5 }, plan, 0);
  assert.ok(Math.abs(mid.x) < 1e-9 && Math.abs(mid.z) < 1e-9 && mid.y === 0);
  const top = placeInHouse({ x: 0, y: 0 }, plan, 2);
  assert.ok(top.x < 0 && top.z < 0);
  assert.equal(top.y, 2 * FLOOR_GAP);
  assert.ok(WALL_HEIGHT > 1 && WALL_HEIGHT < FLOOR_GAP);
});

test("a plan with nothing on it still has somewhere to stand", () => {
  const h = houseOf({ width: 1200, height: 800, unit: "ft", step: 40, perStep: 1, items: [] });
  assert.equal(h.walls.length, 0);
  assert.ok(h.size[0] > 0 && h.size[1] > 0);
});
