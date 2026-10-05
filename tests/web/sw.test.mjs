// The service worker answers for one thing only: a page being opened while the
// server can't be reached. Everything else, above all /api and the sign-in,
// must go to the network untouched. The code is the shipped sw.js. Run from
// the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// sw.js run against a fake worker scope. `network` is what fetch does.
function load(network) {
  const handlers = {}, store = new Map();
  const cache = {
    addAll: async urls => urls.forEach(u => store.set(u, "cached " + u)),
    match: async u => store.get(u),
  };
  const scope = {
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting: async () => {},
    clients: { claim: async () => {} },
    caches: {
      open: async () => cache,
      keys: async () => [],
      delete: async () => true,
      match: async u => store.get(u),
    },
    fetch: network,
  };
  vm.runInNewContext(readFileSync("BAMF/wwwroot/sw.js", "utf8"), { self: scope, ...scope, caches: scope.caches, fetch: network });
  const fire = async req => {
    let answer, claimed = false;
    handlers.fetch({ request: req, respondWith: p => { claimed = true; answer = p; } });
    return { claimed, response: claimed ? await answer : undefined };
  };
  const install = async () => {
    let work; handlers.install({ waitUntil: p => { work = p; } }); await work;
  };
  return { fire, install };
}

const down = async () => { throw new TypeError("offline"); };
const req = (method, url, mode) => ({ method, url, mode });

test("a page opened while the server is down shows the offline page", async () => {
  const sw = load(down);
  await sw.install();
  const r = await sw.fire(req("GET", "/", "navigate"));
  assert.equal(r.claimed, true);
  assert.equal(r.response, "cached /offline.html");
});

test("a page opened while the server is up comes from the server, not the cache", async () => {
  const sw = load(async () => "from the network");
  await sw.install();
  const r = await sw.fire(req("GET", "/", "navigate"));
  assert.equal(r.response, "from the network");
});

test("the API, scripts and sign-in are never answered by the worker", async () => {
  const sw = load(down);
  await sw.install();
  for (const r of [
    req("GET", "/api/hosts", "cors"),
    req("GET", "/js/core.js", "no-cors"),
    req("POST", "/api/signin", "cors"),
    req("POST", "/signin", "navigate"),
    req("DELETE", "/api/hosts/x", "cors"),
  ]) assert.equal((await sw.fire(r)).claimed, false, `${r.method} ${r.url}`);
});
