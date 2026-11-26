// Hide devices unseen for a while: a view setting for the Devices list. The code is read from the dashboard (js/devices.js) and run.
// Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const devices = readFileSync("BAMF/wwwroot/js/devices.js", "utf8");

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

const ago = d => new Date(Date.now() - d * 864e5).toISOString();
const H = (id, name, extra = {}) => ({ id, customName: name, ip: `192.168.10.${id}`, mac: `AA:00:00:00:00:0${id}`, subnet: "192.168.10.0/24", known: true, online: false, lastSeen: ago(1), ...extra });
const hosts = [
  H(1, "laptop", { online: true }),
  H(2, "old phone", { lastSeen: ago(45) }),
  H(3, "last week tv", { lastSeen: ago(8) }),
  H(4, "watched nas", { lastSeen: ago(90), watched: true }),
  H(5, "old ignored", { lastSeen: ago(80), ignored: true }),
  H(6, "never dated", { lastSeen: "" }),
  H(7, "ancient plug", { lastSeen: ago(400) }),
];

function page({ days = 0, show = false, filter = "all", query = "", view = "devices" } = {}) {
  const ctx = vm.createContext({ Date, isNaN });
  vm.runInContext(`
    let hosts = ${JSON.stringify(hosts)}, view = ${JSON.stringify(view)}, filter = ${JSON.stringify(filter)}, query = ${JSON.stringify(query)};
    let hideUnseenDays = ${days}, showUnseen = ${show};
    let guessFilter = null, networkLabels = {};
    const NO_GUESS = "\u0000none";
    function inNetwork() { return true; }
    function tagOk() { return true; }
    function typeFamily() { return null; }
    function isNew() { return false; }
    ${take(devices, "function unseenHidden(")}
    ${take(devices, "function visible(")}
    ${take(devices, "function matchesQuery(")}
    globalThis.names = () => visible().map(h => h.customName);
    globalThis.hiddenCount = () => hosts.filter(h => !h.ignored && !h.forgotten && unseenHidden(h)).length;`, ctx);
  return { names: [...ctx.names()], hidden: ctx.hiddenCount() };
}

test("off, nothing changes", () => {
  assert.deepEqual(page({ days: 0 }).names, ["laptop", "old phone", "last week tv", "watched nas", "never dated", "ancient plug"]);
});

test("on, devices offline longer than the days are left out", () => {
  const r = page({ days: 30 });
  assert.deepEqual(r.names, ["laptop", "last week tv", "watched nas", "never dated"]);
  assert.equal(r.hidden, 2);
  assert.deepEqual(page({ days: 7 }).names, ["laptop", "watched nas", "never dated"]);
  assert.deepEqual(page({ days: 365 }).names, ["laptop", "old phone", "last week tv", "watched nas", "never dated"]);
});

test("a watched device, an online one and one with no last-seen time are never hidden", () => {
  const names = page({ days: 7 }).names;
  assert.ok(names.includes("watched nas") && names.includes("laptop") && names.includes("never dated"));
});

test("the Show chip lets them through, and the Offline tab follows the setting too", () => {
  assert.deepEqual(page({ days: 30, show: true }).names, ["laptop", "old phone", "last week tv", "watched nas", "never dated", "ancient plug"]);
  assert.deepEqual(page({ days: 30, filter: "offline" }).names, ["last week tv", "watched nas", "never dated"]);
});

test("searching finds them, and the Ignored tab is not touched", () => {
  assert.deepEqual(page({ days: 30, query: "old" }).names, ["old phone", "old ignored"]);
  assert.deepEqual(page({ days: 30, filter: "ignored" }).names, ["old ignored"]);
});
