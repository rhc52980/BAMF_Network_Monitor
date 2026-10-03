// Which Map icon a device gets from its name and vendor, and that the icon lists on the server and in the
// dashboard agree. The code is read from the dashboard (js/map.js) and run, so this tests what ships.
// Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const map = readFileSync("BAMF/wwwroot/js/map.js", "utf8");
const devices = readFileSync("BAMF/wwwroot/js/devices.js", "utf8");

// A top-level declaration: to its closing brace for a function, to the end of its brace or bracket otherwise.
function take(src, start) {
  const i = src.indexOf(start);
  assert.notEqual(i, -1, `no "${start}" in the dashboard's scripts`);
  if (start.startsWith("function")) {
    let depth = 0;
    for (let j = src.indexOf("{", i); j < src.length; j++) {
      if (src[j] === "{") depth++;
      else if (src[j] === "}" && --depth === 0) return src.slice(i, j + 1);
    }
    throw new Error(`"${start}" never closes`);
  }
  const line = src.slice(i, src.indexOf("\n", i));
  if (/;\s*$/.test(line)) return line;
  const end = src.slice(i).search(/\r?\n[\]}];/);
  assert.notEqual(end, -1, `"${start}" never ends`);
  return src.slice(i, src.indexOf(";", i + end) + 1);
}

const ctx = vm.createContext({ Array });
vm.runInContext([
  take(map, "const TOPO_ICONS ="), take(map, "const TOPO_KINDS ="), take(map, "const SMALL_KINDS ="), take(map, "const TOPO_REFINE ="),
  take(map, "function refineKind("), take(map, "function topoKind("), take(devices, "const TYPE_DEFS ="),
  "globalThis.page = { TOPO_ICONS, TYPE_DEFS, topoKind };",
].join("\n"), ctx);
const page = ctx.page;

const kindOf = (text, vendor = "", osGuess = "") => page.topoKind({ osGuess, customName: text, hostname: "—", vendor });

test("a device's name and vendor make its plain kind more specific", () => {
  assert.equal(kindOf("Living Room Roku"), "streamer");          // a TV by the old rules
  assert.equal(kindOf("Sonos Beam"), "soundbar");
  assert.equal(kindOf("Front door", "Ring Video Doorbell Pro"), "doorbell");
  assert.equal(kindOf("Hallway", "ecobee"), "thermostat");
  assert.equal(kindOf("roborock-s7"), "vacuum");
  assert.equal(kindOf("rachio-3"), "sprinkler");
  assert.equal(kindOf("Kitchen fridge"), "fridge");
  assert.equal(kindOf("octopi"), "printer3d");
  assert.equal(kindOf("pihole", "Raspberry Pi Trading"), "pi");
  assert.equal(kindOf("Garage", "Chamberlain"), "garage");
  assert.equal(kindOf("Garage", "", "Linux (TTL 64, SSH)"), "server");   // a computer named for a room is still a computer
});

test("a name never turns a firm kind into another: a router stays a router", () => {
  assert.equal(kindOf("Ring doorbell router", "", "Router"), "router");
  assert.equal(kindOf("nest thermostat", "", "Router"), "router");
  assert.equal(kindOf("My laptop", "Honda"), "laptop");
});

test("what the old rules guessed is still guessed", () => {
  for (const [name, kind] of [["Brother printer", "printer"], ["Pixel 8", "phone"], ["MacBook Pro", "laptop"], ["synology", "nas"], ["xbox", "game"], ["Hue bridge", "iot"]])
    assert.equal(kindOf(name), kind, name);
  assert.equal(kindOf(""), "device");
});

test("a Nest Hub is a display, not a thermostat, and a Nest Learning Thermostat is one", () => {
  assert.equal(kindOf("Nest Hub"), "display");
  assert.equal(kindOf("Nest Learning Thermostat"), "thermostat");
});

test("every type has an icon, a name, and a place in the server's list, and the other way round", () => {
  const defs = Array.from(page.TYPE_DEFS, ([k]) => k).sort();
  const icons = Object.keys(page.TOPO_ICONS).filter(k => !["vpn", "net", "vswitch"].includes(k)).sort();   // drawn only, never picked
  assert.deepEqual(defs, icons);
  assert.equal(new Set(defs).size, defs.length);
  const cs = readFileSync("BAMF/Services/HostStore.DeviceTypes.cs", "utf8");
  const block = cs.slice(cs.indexOf("DeviceTypeKeys ="), cs.indexOf("};", cs.indexOf("DeviceTypeKeys =")));
  const server = [...block.matchAll(/"([a-z0-9]+)"/g)].map(m => m[1]).sort();
  assert.deepEqual(server, defs);
  for (const [k, label] of page.TYPE_DEFS) assert.ok(label.length > 1, k);
});

test("every refined kind is one that can be picked", () => {
  const picked = new Set(Array.from(page.TYPE_DEFS, ([k]) => k));
  for (const [kind] of vm.runInContext("TOPO_REFINE", ctx)) assert.ok(picked.has(kind), `${kind} has no icon to pick`);
});
