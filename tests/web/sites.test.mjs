// The Sites card: how each site's devices are counted, and what it says is wrong. The card's code is read from the
// dashboard (js/sites.js) and run. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const ctx = vm.createContext({ fmtAgo: () => "5 min ago" });
vm.runInContext(readFileSync("BAMF/wwwroot/js/sites.js", "utf8") + "\nglobalThis.page = { sitesSummary, siteNote };", ctx);
const { sitesSummary, siteNote } = ctx.page;

const h = (id, more = {}) => ({ id, online: true, known: true, watched: false, ...more });

test("this site and each remote are counted from their own devices", () => {
  const rows = sitesSummary([
    h(1), h(2, { online: false }), h(3, { known: false }),
    h(4, { remote: "Cabin" }), h(5, { remote: "Cabin", online: false, watched: true }), h(6, { remote: "Office" }),
  ], [{ name: "Cabin", ok: true, version: "2.3.0", lastScan: "2026-10-04T09:00:00Z", subnets: ["Cabin · 10.0.0.0/24"] }, { name: "Office", ok: true }], "2026-10-04T09:01:00Z");
  assert.deepEqual(Array.from(rows, r => r.name), ["This site", "Cabin", "Office"]);
  assert.deepEqual([rows[0].total, rows[0].online, rows[0].unknown], [3, 2, 1]);
  assert.deepEqual([rows[1].total, rows[1].online, rows[1].watchedOff], [2, 1, 1]);
  assert.equal(rows[1].net, "Cabin · 10.0.0.0/24");
  assert.equal(rows[2].total, 1);
});

test("ignored and forgotten devices aren't counted", () => {
  const rows = sitesSummary([h(1), h(2, { ignored: true }), h(3, { forgotten: true })], [], null);
  assert.equal(rows[0].total, 1);
});

test("what a site says is wrong: unreachable first, then unknown devices, then a watched one off", () => {
  assert.match(siteNote({ ok: false, fetched: "2026-10-04T08:00:00Z" }), /Not answering\. Showing what it said 5 min ago/);
  assert.match(siteNote({ ok: false, error: "HTTP 401" }), /Not answering: HTTP 401/);
  assert.equal(siteNote({ ok: true, unknown: 2, watchedOff: 1 }), "2 unknown devices are on it");
  assert.equal(siteNote({ ok: true, unknown: 1 }), "1 unknown device is on it");
  assert.equal(siteNote({ ok: true, watchedOff: 1 }), "1 watched device is offline");
  assert.equal(siteNote({ ok: true }), "");
});
