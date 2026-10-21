// The 3D tab's newer layers, on the data side: what would stop if a device died, the usage skyline, the exposure flags, the briefing's
// stops, the weather for a score, and the longer replay. Plain functions in engine3d/data.mjs.
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  buildScene, outageOf, outageText, formatBytes, towerHeight, usageOf, exposureOf, briefingOf, weatherOf, markersOf, replaySpeed, problemsOf,
  USAGE_WINDOWS, REPLAY_RANGES, REPLAY_SECONDS,
} from "../../BAMF/wwwroot/engine3d/data.mjs";

// a router, a switch with two things plugged into it (one watched, one running a service), and a stand-alone device, on one network
function scene() {
  const hosts = [
    { id: 1, ip: "10.0.0.1", mac: "A", subnet: "10.0.0.0/24", online: true, known: true },
    { id: 2, ip: "10.0.0.2", mac: "B", subnet: "10.0.0.0/24", online: true, known: true },
    { id: 3, ip: "10.0.0.3", mac: "C", subnet: "10.0.0.0/24", online: true, known: true, watched: true },
    { id: 4, ip: "10.0.0.4", mac: "D", subnet: "10.0.0.0/24", online: false, known: true },
    { id: 5, ip: "10.0.0.5", mac: "E", subnet: "10.0.0.0/24", online: true, known: true },
  ];
  const parents = { 3: 2, 4: 2 };
  return buildScene({
    hosts, kindOf: h => h.id === 1 ? "router" : h.id === 2 ? "switch" : "server", nameOf: h => "d" + h.id,
    gatewayIp: () => "10.0.0.1", parentOf: h => parents[h.id] ?? null,
    servicesOf: h => h.id === 3 ? [{ name: "Plex", state: "up" }, { name: "SSH", state: "down" }] : [],
  });
}

test("a switch takes everything plugged into it, and its watched devices and services, with it", () => {
  const o = outageOf(scene().devices, "2");
  assert.equal(o.reason, "network");
  assert.deepEqual(o.cut.sort(), ["3", "4"]);
  assert.equal(o.counts.cut, 2);
  assert.equal(o.counts.cutOnline, 1);                       // d4 is already offline
  assert.equal(o.counts.services, 1);                        // Plex would go down; SSH already is
  assert.equal(o.counts.watched, 1);
  assert.deepEqual(o.serviceNames, ["Plex on d3"]);
  assert.match(outageText(o), /2 devices would lose the network \(1 of them online now\)\. 1 watched service would go down, such as Plex on d3\. 1 watched device would show offline and alert\./);
});

test("the gateway leaves its whole network without the internet but takes nothing else down", () => {
  const o = outageOf(scene().devices, "1");
  assert.equal(o.reason, "internet");
  assert.equal(o.internet, true);
  assert.equal(o.counts.cut, 4);
  assert.equal(o.counts.services, 0);                        // the devices stay powered
  assert.equal(o.counts.watched, 0);
  assert.match(outageText(o), /4 devices on 10\.0\.0\.0\/24 would lose the internet\. They'd still reach each other\./);
});

test("a device with nothing plugged into it is only itself, and a key that isn't there is nothing", () => {
  const o = outageOf(scene().devices, "5");
  assert.equal(o.reason, "alone");
  assert.deepEqual(o.cut, []);
  assert.equal(outageText(o), "Nothing else depends on it.");
  assert.equal(outageOf(scene().devices, "nope"), null);
  assert.equal(outageText(null), "");
});

test("an alone device that is watched and running a service counts for itself", () => {
  const devices = scene().devices;
  const d3 = devices.find(d => d.key === "3");
  const o = outageOf(devices, "3");
  assert.equal(o.reason, "alone");
  assert.equal(o.counts.watched, 1);
  assert.equal(o.counts.services, 1);
  assert.equal(d3.parent, "2");
});

test("bytes are written the way they are read", () => {
  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(840), "840 B");
  assert.equal(formatBytes(840_000), "840 kB");
  assert.equal(formatBytes(1_250_000), "1.3 MB");
  assert.equal(formatBytes(48_000_000), "48 MB");
  assert.equal(formatBytes(12_400_000_000), "12 GB");
  assert.equal(formatBytes(3_200_000_000), "3.2 GB");
  assert.equal(formatBytes(2e12), "2.0 TB");
  assert.equal(formatBytes(-5), "0 B");
});

test("a tower is as tall as the square root of its share of the biggest, with a stub for anything that moved", () => {
  assert.equal(towerHeight(0, 100), 0);
  assert.equal(towerHeight(50, 0), 0);
  assert.equal(towerHeight(100, 100), 1);
  assert.ok(Math.abs(towerHeight(25, 100) - 0.5) < 1e-9);
  assert.equal(towerHeight(1, 1_000_000), 0.05);               // a trickle is still visible
  assert.equal(towerHeight(500, 100), 1);
});

test("usage is matched to the devices drawn, biggest first, with the share that was uploaded", () => {
  const devices = scene().devices;
  const u = usageOf(devices, [
    { hostId: 3, rx: 900, tx: 100 }, { hostId: 5, rx: 100, tx: 300 }, { hostId: 99, rx: 1e9, tx: 0 },     // 99 isn't drawn
    { hostId: 2, rx: 0, tx: 0 },
  ], 2);
  assert.deepEqual(u.top.map(r => r.key), ["3", "5"]);
  assert.equal(u.max, 1000);
  assert.equal(u.total, 1400);
  assert.equal(u.byKey.get("3").height, 1);
  assert.ok(Math.abs(u.byKey.get("5").height - Math.sqrt(0.4)) < 1e-9);
  assert.ok(Math.abs(u.byKey.get("5").upShare - 0.75) < 1e-9);
  assert.equal(u.byKey.has("2"), false);                       // it moved nothing
  assert.equal(usageOf(devices, null).max, 0);
  assert.deepEqual(USAGE_WINDOWS.map(w => w.hours), [2, 24, 168]);
});

test("risky ports, a certificate, UPnP and a device's jobs are flagged, worst first", () => {
  assert.deepEqual(exposureOf({}), { risks: [], roles: [], sev: "" });
  const x = exposureOf({ ports: [80, 23, 443, 5900], cert: { days: 6 }, upnp: true, roles: ["DHCP"] });
  assert.deepEqual(x.risks.map(r => r.label), [
    "Telnet is open (port 23)", "VNC is open (port 5900)", "Answers UPnP: any device can ask it to open ports to the internet", "Its certificate expires in 6 days",
  ]);
  assert.equal(x.sev, "high");
  assert.deepEqual(x.roles, ["DHCP"]);
  assert.equal(exposureOf({ cert: { days: 6 } }).sev, "med");
  assert.equal(exposureOf({ cert: { days: 30 } }).sev, "");        // not soon enough to flag
  assert.match(exposureOf({ cert: { days: -3 } }).risks[0].label, /expired 3 days ago/);
  assert.equal(exposureOf({ ports: [80, 443, 22] }).sev, "");       // ordinary ports aren't risks
});

test("the scene carries what exposureOf says about each device", () => {
  const hosts = [{ id: 1, ip: "10.0.0.1", mac: "A", subnet: "10.0.0.0/24", online: true, known: true }, { id: 2, ip: "10.0.0.2", mac: "B", subnet: "10.0.0.0/24", online: true, known: true }];
  const s = buildScene({ hosts, kindOf: () => "server", nameOf: h => "d" + h.id, exposureOf: h => h.id === 2 ? exposureOf({ ports: [21] }) : null });
  assert.equal(s.devices.find(d => d.id === 1).exposure, null);
  assert.equal(s.devices.find(d => d.id === 2).exposure.sev, "high");
});

test("the briefing is what is wrong, what went down, who joined, internet outages and alerts, worst first and not twice", () => {
  const now = Date.parse("2026-10-20T12:00:00Z");
  const hrs = h => new Date(now - h * 3600e3).toISOString();
  const devices = scene().devices;
  const problems = problemsOf(devices.map(d => d.key === "3" ? { ...d, state: "off", watching: true } : d));
  const timeline = {
    from: hrs(24), to: hrs(0),
    hosts: [{ id: 5, first: hrs(3), online0: false }, { id: 2, first: hrs(24), online0: true }, { id: 3, first: hrs(24), online0: true }],
    events: [{ h: 2, type: "offline", at: hrs(6) }, { h: 2, type: "online", at: hrs(5.5) }, { h: 3, type: "offline", at: hrs(2) }],
    outages: [{ start: hrs(8), end: hrs(7.9), local: false }],
  };
  const alerts = [{ kind: "dhcp", title: "New DHCP server: 10.0.0.66", detail: "x", at: hrs(1) }, { kind: "rule", title: "A rule", detail: "", at: hrs(1) }, { kind: "dns", title: "Old", detail: "", at: hrs(40) }];
  const stops = briefingOf({ devices, problems, timeline, alerts, nowMs: now });
  assert.deepEqual(stops.map(s => s.kind), ["down", "internet", "alert", "down", "new"]);
  assert.equal(stops[0].key, "3");                                    // a watched device that is down: from the problems, and not again from the events
  assert.match(stops[0].title, /d3 is down/);
  assert.equal(stops[1].key, "internet");
  assert.match(stops[1].title, /The internet was down for 6 minutes/);
  assert.equal(stops[2].key, null);                                   // an alert about the network, not one device
  assert.match(stops[3].title, /d2 dropped off and came back/);
  assert.match(stops[4].title, /d5 joined the network/);
  assert.equal(stops.filter(s => s.key === "3").length, 1);
  assert.equal(briefingOf({ devices, timeline: { from: hrs(24), to: hrs(0), hosts: [], events: [], outages: [] }, nowMs: now }).length, 0);
  assert.equal(briefingOf({ devices, problems, timeline, alerts, nowMs: now, max: 2 }).length, 2);
});

test("the weather follows the score, and an off score is a fair sky", () => {
  assert.equal(weatherOf(97).key, "clear");
  assert.equal(weatherOf(90).key, "clear");
  assert.equal(weatherOf(89).key, "fair");
  assert.equal(weatherOf(77).key, "fair");
  assert.equal(weatherOf(60).key, "overcast");
  assert.equal(weatherOf(49).key, "storm");
  assert.equal(weatherOf(null).key, "fair");
  assert.equal(weatherOf(97).rain, 0);
  assert.equal(weatherOf(77).rain, 0);
  assert.ok(weatherOf(60).rain > 0 && weatherOf(60).rain < weatherOf(20).rain);
  assert.equal(weatherOf(60).storm, 0);
  assert.ok(weatherOf(30).storm > 0.4);
  assert.ok(weatherOf(20).cloud > weatherOf(95).cloud);
  assert.ok(weatherOf(20).tint[0] < weatherOf(95).tint[0]);
  assert.ok(weatherOf(120).score === 100 && weatherOf(-5).score === 0);
});

test("the replay's ranges and speeds, and the marks along its slider", () => {
  assert.deepEqual(REPLAY_RANGES.map(r => r.hours), [24, 72, 168, 720]);
  assert.deepEqual(REPLAY_SECONDS.map(s => s.s), [12, 60, 180]);
  assert.equal(replaySpeed(24 * 3600e3, 60), 1440 * 1000);
  assert.equal(replaySpeed(10, 1000), 1);                                // never slower than real time
  const from = Date.parse("2026-10-20T00:00:00Z"), at = h => new Date(from + h * 3600e3).toISOString();
  const marks = markersOf({
    from: at(0), to: at(10),
    hosts: [{ id: 1, first: at(0) }, { id: 2, first: at(5) }],
    outages: [{ start: at(2), end: at(2.2) }, { start: at(-3), end: at(-2) }],
    unusual: [{ hostId: 1, at: at(8), title: "workshop-pi is off" }],
  });
  assert.deepEqual(marks.map(m => [m.kind, +m.at.toFixed(2)]), [["outage", 0.2], ["arrive", 0.5], ["unusual", 0.8]]);
  assert.equal(marks[2].label, "workshop-pi is off");
  assert.deepEqual(markersOf({ from: at(0), to: at(0), hosts: [] }), []);
});
