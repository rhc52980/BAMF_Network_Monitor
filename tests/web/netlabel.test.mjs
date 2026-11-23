// How a named network is shown: the name, then its range; the range alone when it has no name. The code is read from the
// dashboard (js/core.js) and run. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const core = readFileSync("BAMF/wwwroot/js/core.js", "utf8");
const i = core.indexOf("function netName(");
assert.notEqual(i, -1, "no netName in the dashboard's scripts");
const fn = core.slice(i, core.indexOf("\n", i));

function page(labels) {
  const ctx = vm.createContext({});
  vm.runInContext(`let networkLabels = ${JSON.stringify(labels)};\n${fn}\nglobalThis.netName = netName;`, ctx);
  return ctx.netName;
}

test("a named network shows its name and then its range", () => {
  const netName = page({ "192.168.30.0/24": "IoT" });
  assert.equal(netName("192.168.30.0/24"), "IoT · 192.168.30.0/24");
});

test("a network with no name shows just its range, and so does one from a remote site", () => {
  const netName = page({ "192.168.30.0/24": "IoT" });
  assert.equal(netName("192.168.2.0/24"), "192.168.2.0/24");
  assert.equal(netName("Cabin · 10.0.0.0/24"), "Cabin · 10.0.0.0/24");
});

test("a name that is empty counts as no name", () => {
  assert.equal(page({ "192.168.30.0/24": "" })("192.168.30.0/24"), "192.168.30.0/24");
});
