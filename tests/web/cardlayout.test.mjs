// How the Activity tab's cards are arranged: what a saved layout means once cards have been added or taken away, moving one card, and that the
// page code loads without a document. The rules are plain functions in js/cardlayout.js, run here as they are in the dashboard.
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const ctx = vm.createContext({});
vm.runInContext(readFileSync("BAMF/wwwroot/js/cardlayout.js", "utf8") + "\nglobalThis.page = { cleanCardLayout, mergeCardOrder, moveCardBefore, stepCard, CARD_LAYOUT_KEY };", ctx);
const { cleanCardLayout, mergeCardOrder, moveCardBefore, stepCard, CARD_LAYOUT_KEY } = ctx.page;
const arr = x => Array.from(x);

const DEFAULT = ["score", "unusual", "changes", "alerts", "wan", "services"];

test("a saved layout keeps only cards that exist, each once, and a hidden card isn't also folded", () => {
  const l = cleanCardLayout({ order: ["wan", "ghost", "wan", "score"], hidden: ["alerts", 7], folded: ["alerts", "services", "nope"] }, DEFAULT);
  assert.deepEqual(arr(l.order), ["wan", "score"]);
  assert.deepEqual(arr(l.hidden), ["alerts"]);
  assert.deepEqual(arr(l.folded), ["services"]);
});

test("nothing saved, or something that isn't a layout, is an empty one", () => {
  for (const raw of [null, undefined, "x", 5, [], {}, { order: "score", hidden: 3 }]) {
    const l = cleanCardLayout(raw, DEFAULT);
    assert.deepEqual([arr(l.order), arr(l.hidden), arr(l.folded)], [[], [], []]);
  }
});

test("the saved order is kept, and the rest follow in the page's own order", () => {
  assert.deepEqual(arr(mergeCardOrder(DEFAULT, [])), DEFAULT);
  assert.deepEqual(arr(mergeCardOrder(DEFAULT, null)), DEFAULT);
  const mine = ["wan", "score", "services", "unusual", "changes", "alerts"];
  assert.deepEqual(arr(mergeCardOrder(DEFAULT, mine)), mine);
  // only some are named: those in that order, and each of the others straight after the card that came before it by default
  // (so "services" follows "wan", the card before it, wherever the saved order put that)
  assert.deepEqual(arr(mergeCardOrder(DEFAULT, ["wan", "score"])), ["wan", "services", "score", "unusual", "changes", "alerts"]);
});

test("a card added by an update goes after the one before it in the default order, and one the update took away is dropped", () => {
  const grown = ["score", "compare", "unusual", "changes", "alerts", "wan", "services"];
  // saved before "compare" existed, with the user's own order
  assert.deepEqual(arr(mergeCardOrder(grown, ["wan", "alerts", "score", "unusual", "changes", "services"])), ["wan", "alerts", "score", "compare", "unusual", "changes", "services"]);
  // a new card at the front of the defaults goes first
  assert.equal(mergeCardOrder(["sites", ...DEFAULT], ["wan", "score"])[0], "sites");
  // a saved card the page no longer has is left out
  assert.deepEqual(arr(mergeCardOrder(DEFAULT.filter(x => x !== "alerts"), ["alerts", "wan", "score"])), ["wan", "services", "score", "unusual", "changes"]);
  // each card exactly once, even from a saved list with repeats
  assert.equal(new Set(mergeCardOrder(grown, ["wan", "wan", "score"])).size, grown.length);
  assert.equal(mergeCardOrder(grown, ["wan", "wan", "score"]).length, grown.length);
});

test("dropping a card before another puts it there, and with nothing after puts it last", () => {
  assert.deepEqual(arr(moveCardBefore(DEFAULT, "wan", "score")), ["wan", "score", "unusual", "changes", "alerts", "services"]);
  assert.deepEqual(arr(moveCardBefore(DEFAULT, "score", "services")), ["unusual", "changes", "alerts", "wan", "score", "services"]);
  assert.deepEqual(arr(moveCardBefore(DEFAULT, "score", null)), ["unusual", "changes", "alerts", "wan", "services", "score"]);
  assert.equal(moveCardBefore(DEFAULT, "alerts", "nowhere").at(-1), "alerts");           // a target that isn't there is the end
  assert.equal(moveCardBefore(DEFAULT, "alerts", "wan").length, DEFAULT.length);          // never loses or doubles a card
});

test("the arrows move a card one place and stop at the ends", () => {
  assert.deepEqual(arr(stepCard(DEFAULT, "changes", -1)), ["score", "changes", "unusual", "alerts", "wan", "services"]);
  assert.deepEqual(arr(stepCard(DEFAULT, "changes", 1)), ["score", "unusual", "alerts", "changes", "wan", "services"]);
  assert.deepEqual(arr(stepCard(DEFAULT, "score", -1)), DEFAULT);
  assert.deepEqual(arr(stepCard(DEFAULT, "services", 1)), DEFAULT);
  assert.deepEqual(arr(stepCard(DEFAULT, "ghost", 1)), DEFAULT);
  assert.notEqual(stepCard(DEFAULT, "score", -1), DEFAULT);                                   // a copy, never the list it was given
});

test("the page code loads without a document, and keeps its layout under one key", () => {
  assert.equal(CARD_LAYOUT_KEY, "bamf-activity-layout");
});
