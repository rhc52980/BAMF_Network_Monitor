// Marking what is new on Activity: a browser setting. The code is read from the dashboard (js/activity.js) and run.
// Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const src = readFileSync("BAMF/wwwroot/js/activity.js", "utf8");

function take(start) {
  const i = src.indexOf(start);
  assert.notEqual(i, -1, `no "${start}" in activity.js`);
  let depth = 0;
  for (let j = src.indexOf("{", i); j < src.length; j++) {
    if (src[j] === "{") depth++;
    else if (src[j] === "}" && --depth === 0) return src.slice(i, j + 1);
  }
  throw new Error(`"${start}" never closes`);
}
function between(a, b) {
  const i = src.indexOf(a), j = src.indexOf(b);
  assert.ok(i !== -1 && j > i, `can't find ${a} .. ${b}`);
  return src.slice(i, j);
}

// Server times look like this (seven fractional digits), so that is what the harness uses.
const t = min => new Date(Date.UTC(2026, 10, 27, 12, 0, 0) + min * 60e3).toISOString().replace("Z", "0000Z");

// A tiny page: storage, a view, the elements the two cards write to, and what the server would send.
function page({ marks = true, seen = t(0), view = "activity", alerts = [], problems = [], server = { alerts: 0, problems: 0, now: t(60) } } = {}) {
  const store = new Map();
  if (marks) store.set("bamf-activity-marks", "on");
  if (seen) store.set("bamf-activity-seen", seen);
  const els = {};
  const ctx = vm.createContext({
    Date, isNaN, JSON, Number, String, Math,
    localStorage: { getItem: k => store.get(k) ?? null, setItem: (k, v) => store.set(k, v) },
    fetch: async () => ({ ok: true, json: async () => server }),
  });
  vm.runInContext(`
    const els = {};
    const $ = id => els[id] || (els[id] = { hidden: false, textContent: "", innerHTML: "", title: "", classList: { toggle() {} } });
    const esc = s => String(s ?? "");
    const fmtAgo = at => "earlier";
    let view = ${JSON.stringify(view)}, role = "admin", alertsCache = ${JSON.stringify(alerts)};
    ${between("const ACTIVITY_SEEN_KEY", "// ---- Disk and database")}
    ${between("// ---- Problems:", "$(\"problemsClear\").onclick")}
    ${take("function renderAlertsCard(")}
    problemsCache = ${JSON.stringify({ since: t(-600), rows: problems })};
    globalThis.go = v => { view = v; };
    globalThis.alertsHtml = () => { renderAlertsCard(); return $("alertsBody").innerHTML; };
    globalThis.problemsHtml = () => { renderProblems(); return $("problemsBody").innerHTML; };
    globalThis.badge = () => ({ hidden: $("activityCount").hidden, text: $("activityCount").textContent });
    globalThis.newSince = () => activityNewSince;`, ctx);
  return { ctx, store };
}

const A = (title, min, kind = "rule") => ({ at: t(min), kind, title, detail: "d" });
const count = (html, what) => html.split(what).length - 1;

test("off, nothing is tagged and the lists look as they always did", async () => {
  const { ctx } = page({ marks: false, alerts: [A("old", -5), A("fresh", 30)] });
  await ctx.loadUnseen();
  assert.equal(ctx.newSince(), null);
  const html = ctx.alertsHtml();
  assert.equal(count(html, "new-tag"), 0);
  assert.equal(count(html, "new-note"), 0);
});

test("opening Activity tags what is newer than the last look, and says how many", async () => {
  const { ctx } = page({ alerts: [A("fresh one", 30), A("fresh two", 10), A("old", -5)], problems: [{ level: "error", source: "ScannerService", message: "hook refused", first: t(20), last: t(25), count: 1 }, { level: "warn", source: "WanWatch", message: "stale", first: t(-90), last: t(-80), count: 2 }] });
  await ctx.loadUnseen();
  const html = ctx.alertsHtml();
  assert.equal(count(html, "new-tag"), 3);                         // two rows and the line above
  assert.match(html, /2 since you last looked/);
  assert.equal(count(html, "is-new"), 2);
  const p = ctx.problemsHtml();
  assert.equal(count(p, "is-new"), 1);
  assert.match(p, /1 since you last looked/);
});

test("the tags last the whole visit even though 'seen' moves to now, and a refresh does not clear them", async () => {
  const { ctx, store } = page({ alerts: [A("fresh", 30)] });
  await ctx.loadUnseen();
  assert.equal(store.get("bamf-activity-seen"), t(60));            // moved to now straight away
  await ctx.loadUnseen(); await ctx.loadUnseen();                   // the poll runs again
  assert.equal(ctx.newSince(), t(0));
  assert.equal(count(ctx.alertsHtml(), "is-new"), 1);
});

test("leaving Activity lets go, so the next visit marks only what came after the last one", async () => {
  const { ctx } = page({ alerts: [A("fresh", 30), A("later", 90)] });
  await ctx.loadUnseen();
  ctx.go("devices");
  await ctx.loadUnseen();
  assert.equal(ctx.newSince(), null);
  ctx.go("activity");
  await ctx.loadUnseen();
  assert.equal(ctx.newSince(), t(60));                              // the visit before
  const html = ctx.alertsHtml();
  assert.equal(count(html, "is-new"), 1);                           // only "later"
});

test("the very first visit marks nothing, and a Wake note is never marked", async () => {
  const first = page({ seen: null, alerts: [A("fresh", 30)] });
  await first.ctx.loadUnseen();
  assert.equal(first.ctx.newSince(), null);
  assert.equal(count(first.ctx.alertsHtml(), "new-tag"), 0);
  const wake = page({ alerts: [A("woke the NAS", 30, "wake"), A("fresh", 31)] });
  await wake.ctx.loadUnseen();
  assert.equal(count(wake.ctx.alertsHtml(), "is-new"), 1);
});

test("new ones beyond the twelve listed are counted in a line at the end", async () => {
  const alerts = Array.from({ length: 15 }, (_, i) => A("a" + i, 50 - i));
  const { ctx } = page({ alerts });
  await ctx.loadUnseen();
  const html = ctx.alertsHtml();
  assert.equal(count(html, "alert-item"), 12);
  assert.match(html, /15 since you last looked/);
  assert.match(html, /And 3 more new, older than these/);
});

test("the number on the tab still works as before", async () => {
  const { ctx } = page({ view: "devices", server: { alerts: 2, problems: 1, now: t(60) } });
  await ctx.loadUnseen();
  assert.deepEqual({ ...ctx.badge() }, { hidden: false, text: 3 });
});
