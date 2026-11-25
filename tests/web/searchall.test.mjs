// The device list's search: typing looks at every status, ignored devices too, while browsing a status tab still filters by it.
// The code is read from the dashboard (js/devices.js) and run. Run from the repo root:
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

const H = (id, name, extra = {}) => ({ id, customName: name, ip: `192.168.10.${id}`, mac: `AA:00:00:00:00:0${id}`, subnet: "192.168.10.0/24", known: true, online: true, ...extra });
const hosts = [
  H(1, "garage light"),
  H(2, "garage cam", { online: false }),
  H(3, "garage plug", { ignored: true }),
  H(4, "garage old tv", { forgotten: true }),
  H(5, "kitchen speaker", { ignored: true }),
  H(6, "garage stranger", { known: false }),
];

function page({ view = "devices", filter = "all", query = "" } = {}) {
  const ctx = vm.createContext({});
  vm.runInContext(`
    let hosts = ${JSON.stringify(hosts)}, view = ${JSON.stringify(view)}, filter = ${JSON.stringify(filter)}, query = ${JSON.stringify(query)};
    let guessFilter = null, networkLabels = {};
    const NO_GUESS = "\u0000none";
    function inNetwork() { return true; }
    function tagOk() { return true; }
    function typeFamily() { return null; }
    function isNew() { return false; }
    ${take(devices, "function visible(")}
    ${take(devices, "function matchesQuery(")}
    globalThis.names = () => visible().map(h => h.customName);`, ctx);
  return [...ctx.names()];
}

test("without a search, a status tab shows only its own, and ignored ones only on the Ignored tab", () => {
  assert.deepEqual(page({ filter: "all" }), ["garage light", "garage cam", "garage stranger"]);
  assert.deepEqual(page({ filter: "online" }), ["garage light", "garage stranger"]);
  assert.deepEqual(page({ filter: "ignored" }), ["garage plug", "kitchen speaker"]);
});

test("a search finds ignored devices from any tab", () => {
  assert.deepEqual(page({ filter: "all", query: "garage" }), ["garage light", "garage cam", "garage plug", "garage stranger"]);
  assert.deepEqual(page({ filter: "online", query: "garage" }), ["garage light", "garage cam", "garage plug", "garage stranger"]);
  assert.deepEqual(page({ filter: "offline", query: "speaker" }), ["kitchen speaker"]);
});

test("a search ignores the status tab it is on, so Unknown does not hide a known device", () => {
  assert.deepEqual(page({ filter: "unknown", query: "light" }), ["garage light"]);
});

test("forgotten devices stay in the Forgotten view, search or not", () => {
  assert.ok(!page({ query: "garage" }).includes("garage old tv"));
  assert.deepEqual(page({ view: "forgotten", query: "garage" }), ["garage old tv"]);
});
