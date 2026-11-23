// The Map's Signal colours switch: which Wi-Fi devices get a colour, and that a colour goes when the reason for it does. The code
// is read from the dashboard (js/map.js) and run. Run from the repo root:
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

function page(on) {
  const ctx = vm.createContext({});
  vm.runInContext(`let mapSignal = ${on};\n` + [take(map, "function hasSignal("), take(map, "function sigLevel("), take(map, "function sigApply(")].join("\n")
    + "\nglobalThis.page = { hasSignal, sigLevel, sigApply };", ctx);
  return ctx.page;
}
// An element with just a class list.
function el(...cls) {
  const set = new Set(cls);
  return { classList: { add: c => set.add(c), remove: c => set.delete(c), contains: c => set.has(c), [Symbol.iterator]: () => set[Symbol.iterator]() }, get cls() { return [...set].sort(); } };
}
const wifi = (signal, quality) => ({ online: true, wifi: { signal, quality } });

test("a device has a signal level only when the switch is on, it is online, and the controller gave a reading", () => {
  const on = page(true), off = page(false);
  assert.equal(on.sigLevel(wifi(-48, "excellent")), "excellent");
  assert.equal(on.sigLevel(wifi(-80, "weak")), "weak");
  assert.equal(off.sigLevel(wifi(-48, "excellent")), "");                       // off: the Map looks as it did
  assert.equal(on.sigLevel({ online: false, wifi: { signal: -60, quality: "good" } }), "");   // a reading from when it left says nothing
  assert.equal(on.sigLevel({ online: true, wifi: { signal: null, quality: "" } }), "");        // no reading
  assert.equal(on.sigLevel({ online: true, wifi: null }), "");                  // not a Wi-Fi device
  assert.equal(on.sigLevel({ online: true }), "");
  assert.equal(on.sigLevel(null), "");
});

test("a line or tile takes its level's class, replaces an old one, and drops it when there is none", () => {
  const on = page(true);
  const e = el("spoke", "bolt");
  assert.equal(on.sigApply(e, wifi(-72, "fair")), "fair");
  assert.deepEqual(e.cls, ["bolt", "sig-fair", "spoke"]);
  on.sigApply(e, wifi(-85, "weak"));
  assert.deepEqual(e.cls, ["bolt", "sig-weak", "spoke"]);                       // not both
  on.sigApply(e, { online: false, wifi: { signal: -85, quality: "weak" } });
  assert.deepEqual(e.cls, ["bolt", "spoke"]);                                   // it went offline
});
