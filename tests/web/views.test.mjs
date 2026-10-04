// The Views menu: how a view reads, and how the list's current state becomes one and back. The menu's code is read
// from the dashboard (js/views.js) and run, with the page state it reads made settable. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function page(state = {}) {
  const el = () => ({ onclick: null, classList: { toggle() {}, remove() {} }, innerHTML: "", appendChild() {}, children: [], value: "" });
  const els = {};
  const ctx = vm.createContext({
    NO_GUESS: "\u0000none", role: "admin", toolsOpen: false, view: "devices", network: "all", filter: "all", guessFilter: null, tagFilter: null, query: "",
    $: id => (els[id] ||= el()), document: { addEventListener() {}, querySelectorAll: () => [] }, fetch: async () => ({ ok: false }),
    showView(v) { ctx.shown = v; }, ...state,
  });
  vm.runInContext(readFileSync("BAMF/wwwroot/js/views.js", "utf8") + "\nglobalThis.page = { currentViewState, viewSummary, applyView };", ctx);
  return ctx;
}

test("what the list is showing becomes a view, and the no-guess chip becomes a word the server can store", () => {
  const p = page({ view: "forgotten", network: "192.168.30.0/24", filter: "offline", guessFilter: "\u0000none", tagFilter: "kids", query: "*.24" });
  assert.deepEqual({ ...p.page.currentViewState() }, { tab: "forgotten", network: "192.168.30.0/24", status: "offline", guess: "__none__", tag: "kids", query: "*.24" });
  const q = page();
  assert.deepEqual({ ...q.page.currentViewState() }, { tab: "devices", network: "all", status: "all", guess: "", tag: "", query: "" });
});

test("a view reads as the things it narrows to, and as Everything when it narrows nothing", () => {
  const { viewSummary } = page().page;
  assert.equal(viewSummary({ tab: "devices", network: "all", status: "all", guess: "", tag: "", query: "" }), "Everything");
  assert.equal(viewSummary({ tab: "devices", network: "192.168.30.0/24", status: "offline", guess: "", tag: "kids", query: "" }), "Offline · 192.168.30.0/24 · tag kids");
  assert.equal(viewSummary({ tab: "forgotten", network: "all", status: "all", guess: "__none__", tag: "", query: "tv" }), "Forgotten · no guess · “tv”");
});

test("applying a view sets every part of the list and goes to its tab", () => {
  const p = page({ filter: "online", tagFilter: "old", query: "stale" });
  p.page.applyView({ tab: "forgotten", network: "Cabin · 10.0.0.0/24", status: "unknown", guess: "__none__", tag: "", query: "" });
  assert.equal(p.network, "Cabin · 10.0.0.0/24");
  assert.equal(p.filter, "unknown");
  assert.equal(p.guessFilter, "\u0000none");
  assert.equal(p.tagFilter, null);
  assert.equal(p.query, "");
  assert.equal(p.shown, "forgotten");
});
