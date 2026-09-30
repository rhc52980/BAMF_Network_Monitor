// Which devices the themes treat as intruders, and what the watcher each
// scene uses reports as they come and go. The code is taken from the
// dashboard itself, so these test what ships. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// The dashboard's scripts, in the order the page loads them.
const page = readFileSync("BAMF/wwwroot/index.html", "utf8");
const html = [...page.matchAll(/<script\s+src="([^"]+)"><\/script>/g)]
  .map(m => readFileSync("BAMF/wwwroot/" + m[1], "utf8")).join("\n");

// A top-level declaration from the page, up to where it ends: its closing
// brace for a function, the end of the line otherwise.
function take(start) {
  const i = html.indexOf(start);
  assert.notEqual(i, -1, `the dashboard's scripts no longer have "${start}"`);
  if (!start.startsWith("function")) return html.slice(i, html.indexOf("\n", i));
  let depth = 0;
  for (let j = html.indexOf("{", i); j < html.length; j++) {
    if (html[j] === "{") depth++;
    else if (html[j] === "}" && --depth === 0) return html.slice(i, j + 1);
  }
  throw new Error(`"${start}" never closes`);
}

// The dashboard's own isNew, intruders and intruderWatch, with the page state
// they read made settable.
function dashboard() {
  const code = [
    "var hosts = []; var newDays = 7;",
    take("const isNew ="),
    take("let intruderTest ="),
    take("const INTRUDER_TEST_MS ="),
    take("function intruders()"),
    take("function intruderWatch()"),
    "globalThis.page = { intruders, intruderWatch, isNew,",
    "  set hosts(h) { hosts = h; }, set newDays(d) { newDays = d; },",
    "  set test(t) { intruderTest = t; }, get test() { return intruderTest; } };",
  ].join("\n");
  const ctx = vm.createContext({ Date, String });
  vm.runInContext(code, ctx);
  return ctx.page;
}

const DAY = 864e5;
const ago = days => new Date(Date.now() - days * DAY).toISOString();
const host = (id, more = {}) => ({ id, ip: `10.0.0.${id}`, mac: `aa:bb:cc:00:00:${id}`, firstSeen: ago(1), known: false, ...more });
// Made here, not in the page's sandbox, so they compare as plain arrays.
const ids = list => Array.from(list, h => h.id);

test("a new device nobody has marked known is an intruder", () => {
  const p = dashboard();
  p.hosts = [host(1)];
  assert.deepEqual(ids(p.intruders()), [1]);
});

test("a device marked known isn't", () => {
  const p = dashboard();
  p.hosts = [host(1, { known: true })];
  assert.deepEqual(ids(p.intruders()), []);
});

test("nor is one that's ignored, forgotten or on another site", () => {
  const p = dashboard();
  p.hosts = [host(1, { ignored: true }), host(2, { forgotten: true }), host(3, { remote: "Cabin" })];
  assert.deepEqual(ids(p.intruders()), []);
});

test("one seen before the New tab's days is let go", () => {
  const p = dashboard();
  p.hosts = [host(1, { firstSeen: ago(8) }), host(2, { firstSeen: ago(6) })];
  assert.deepEqual(ids(p.intruders()), [2]);
  p.newDays = 30;
  assert.deepEqual(ids(p.intruders()), [1, 2]);
});

test("the oldest comes first", () => {
  const p = dashboard();
  p.hosts = [host(1, { firstSeen: ago(1) }), host(2, { firstSeen: ago(3) }), host(3, { firstSeen: ago(2) })];
  assert.deepEqual(ids(p.intruders()), [2, 3, 1]);
});

test("the watcher reports a new intruder, holds it, then reports it cleared", () => {
  const p = dashboard();
  const watch = p.intruderWatch();
  p.hosts = [host(1)];
  let w = watch();
  assert.deepEqual([ids(w.added), ids(w.held), ids(w.cleared)], [[1], [1], []]);
  w = watch();
  assert.deepEqual([ids(w.added), ids(w.held), ids(w.cleared)], [[], [1], []]);
  p.hosts = [host(1, { known: true })];
  w = watch();
  assert.deepEqual([ids(w.added), ids(w.held), ids(w.cleared)], [[], [], [1]]);
});

test("each scene's watcher keeps its own count", () => {
  const p = dashboard();
  const a = p.intruderWatch(), b = p.intruderWatch();
  p.hosts = [host(1)];
  a();
  assert.deepEqual(ids(b().added), [1]);
});

test("Show me an intruder borrows a device until its time is up", () => {
  const p = dashboard();
  p.hosts = [host(5, { known: true })];
  p.test = { host: host(5, { known: true }), until: Date.now() + 25e3 };
  const list = p.intruders();
  assert.deepEqual(ids(list), [5]);
  assert.equal(list[0].test, true);
  p.test = { host: host(5, { known: true }), until: Date.now() - 1 };
  assert.deepEqual(ids(p.intruders()), []);
  assert.equal(p.test, null);
});

test("a test on a device that's already an intruder doesn't hold it twice", () => {
  const p = dashboard();
  p.hosts = [host(1)];
  p.test = { host: host(1), until: Date.now() + 25e3 };
  assert.deepEqual(ids(p.intruders()), [1]);
});
