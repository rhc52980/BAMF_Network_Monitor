// What the Map's right-click menu offers for each kind of thing, and what its "put back" item saves. The code is read from the
// dashboard (js/mapmenu.js) and run with the page state it reads made settable. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function page(state = {}) {
  const calls = [];
  const noop = () => { };
  const ctx = vm.createContext({
    Map, Set, Object, Promise, console,
    localStorage: { getItem: () => null }, document: { addEventListener: noop, removeEventListener: noop }, window: { addEventListener: noop, removeEventListener: noop },
    role: "admin", networkToolsEnabled: true, mapDragging: false, mapSel: {}, hosts: [], switches: [],
    SEL_TOOLS: [["left", "Align left edges", "", 2], ["hcenter", "Centre across", "", 2], null, ["dh", "Space evenly across", "", 3]],
    arrPicked: (m, keys) => keys.filter(k => m.nodes.has(k)),
    kindNoun: k => ({ ap: "access point", ssid: "wireless SSID", switch: "switch" }[k] || k),
    nameOrIp: h => h.name || h.ip, esc: s => String(s), toast: noop, copyText: async () => true,
    topoCommit: async (...a) => { calls.push(["commit", ...a]); }, topoAlign: noop, topoSelect: noop,
    jumpToHost: noop, openPlugDialog: noop, openTypeDialog: noop, openKindDialog: noop, openNoteDialog: noop, openSnoozeDialog: noop,
    openGatewayDialog: noop, openPortsDialog: noop, openSwitchDialog: noop, openSnmpDialog: noop, openTools: noop, showView: noop,
    setKnown: noop, setIgnored: noop, refresh: noop, fetch: noop, topoModels: {}, mapSig: null,
    ...state,
  });
  vm.runInContext(readFileSync("BAMF/wwwroot/js/mapmenu.js", "utf8") + "\nglobalThis.page = { mapMenuItems };", ctx);
  return { ...ctx.page, ctx, calls };
}
// Made here, so they compare as plain arrays.
const labels = spec => Array.from(spec.items, i => i.sep ? "---" : i.label);
const host = (id, more = {}) => ({ id, ip: `10.0.0.${id}`, mac: `aa:bb:cc:00:00:0${id}`, name: "dev" + id, online: true, known: true, ...more });
const model = (nodes, saved = {}) => ({ subnet: "10.0.0.0/24", nodes: new Map(nodes), saved });

test("a device offers the things you can do to it, by what is true of it", () => {
  const h = host(1, { switchId: 7, known: false });
  const p = page({ hosts: [h], switches: [{ id: 7, name: "Closet", kind: "switch" }] });
  const spec = p.mapMenuItems(model([["h:1", { type: "dev", host: h }]]), "h:1");
  assert.equal(spec.head.title, "dev1");
  assert.equal(spec.head.on, true);
  const l = labels(spec);
  for (const want of ["Open in the devices list", "Change plugged into…", "Unplug from Closet", "Map icon…", "Mark as known", "Ignore", "Show in 3D", "Ping…", "Copy IP address", "Copy MAC address"])
    assert.ok(l.includes(want), want);
  assert.ok(!l.includes("Put back in its automatic place"));                    // it has no place of its own
  assert.ok(!l.includes("Mark as unknown"));
});

test("a device the gear placed can't be unplugged here, and one with no place says Plugged into", () => {
  const placed = host(1, { switchId: 7, switchAuto: "unifi" }), loose = host(2);
  const p = page({ hosts: [placed, loose], switches: [{ id: 7, name: "Closet", kind: "switch" }] });
  const m = model([["h:1", { type: "dev", host: placed }], ["h:2", { type: "dev", host: loose }]]);
  assert.ok(!labels(p.mapMenuItems(m, "h:1")).some(x => x.startsWith("Unplug")));
  assert.ok(labels(p.mapMenuItems(m, "h:2")).includes("Plugged into…"));
});

test("someone who can only look gets the read-only items", () => {
  const h = host(1);
  const p = page({ hosts: [h], role: "viewer" });
  assert.deepEqual(labels(p.mapMenuItems(model([["h:1", { type: "dev", host: h }]]), "h:1")),
    ["Open in the devices list", "---", "Show in 3D", "---", "Copy IP address", "Copy MAC address"]);
});

test("a gateway that was never scanned has no menu, and an unknown key has none", () => {
  const p = page();
  assert.equal(p.mapMenuItems(model([["root", { type: "root", host: { id: 0, ip: "10.0.0.1", synthetic: true } }]]), "root"), null);
  assert.equal(p.mapMenuItems(model([]), "h:9"), null);
});

test("a switch and an SSID offer their own dialogs, and no port count for an SSID", () => {
  const ap = { id: 3, name: "Hall AP", kind: "ap", ports: 0 }, ssid = { id: 4, name: "Home", kind: "ssid", ports: 1, runsOn: 3 };
  const p = page({ switches: [ap, ssid] });
  const m = model([["s:3", { type: "switch", sw: ap, host: null }], ["s:4", { type: "switch", sw: ssid, host: null }]]);
  const a = p.mapMenuItems(m, "s:3"), s = p.mapMenuItems(m, "s:4");
  assert.deepEqual(labels(a), ["Ports…", "Add a wireless SSID…", "Edit access point…"]);
  assert.deepEqual(labels(s), ["Devices on it…", "Edit wireless SSID…"]);
  assert.equal(s.head.sub, "wireless SSID");
});

test("with several things picked the menu is for all of them, and you can only align what there is to align", () => {
  const h1 = host(1), h2 = host(2);
  const nodes = [["h:1", { type: "dev", host: h1 }], ["h:2", { type: "dev", host: h2 }]];
  const m = model(nodes, { "h:1": [10, 10] });
  const p = page({ hosts: [h1, h2], mapSel: { [m.subnet]: new Set(["h:1", "h:2"]) } });
  const spec = p.mapMenuItems(m, "h:1");
  assert.equal(spec.head.title, "2 selected");
  const l = labels(spec);
  assert.ok(l.includes("Align left edges") && l.includes("Centre across"));
  assert.ok(!l.includes("Space evenly across"));                                // that needs three
  assert.ok(l.includes("Put them back in their automatic places") && l.includes("Let go of the selection"));
});

test("Put back saves a null for the thing's own place, as one step that can be undone", async () => {
  const h = host(1);
  const m = model([["h:1", { type: "dev", host: h }]], { "h:1": [100, 50] });
  const p = page({ hosts: [h] });
  const item = p.mapMenuItems(m, "h:1").items.find(i => i.label === "Put back in its automatic place");
  await item.fn();
  assert.deepEqual(JSON.parse(JSON.stringify(p.calls)), [["commit", "10.0.0.0/24", { "h:1": null }, "Put back in its automatic place", true]]);
});
