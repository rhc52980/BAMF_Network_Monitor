// Which devices the Network services card says offer which service. The card's own code is read from the
// dashboard (js/services.js) and run with the page state it reads made settable. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function card(hosts, inNetwork = () => true) {
  const esc = x => String(x ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const ctx = vm.createContext({ String, Map, Set, esc, fmtAgo: () => "3 min ago", fetch: async () => ({ ok: false }) });
  ctx.hosts = hosts; ctx.inNetwork = inNetwork;
  vm.runInContext(readFileSync("BAMF/wwwroot/js/services.js", "utf8") + "\nglobalThis.page = { servicesOnDevices, PORT_SERVICES, MDNS_SERVICES, connectionsHtml, set conns(c) { connectionsCache = c; } };", ctx);
  return ctx.page;
}
const host = (id, more = {}) => ({ id, ip: `10.0.0.${id}`, online: true, openPorts: [], mdnsServices: "", ...more });
// Made here, so they compare as plain arrays.
const summary = list => Array.from(list, s => [s.label, [...s.hosts.keys()].sort((a, b) => a - b)]);

test("open ports and mDNS services are named, and a service is listed once with every device that offers it", () => {
  const page = card([
    host(1, { openPorts: [80, 22] }), host(2, { openPorts: [8080, 443] }),
    host(3, { mdnsServices: "_airplay._tcp,_googlecast._tcp" }), host(4, { mdnsServices: "_airplay._tcp", openPorts: [22] }),
  ]);
  const got = Object.fromEntries(summary(page.servicesOnDevices()));
  assert.deepEqual(got["Web page (HTTP)"], [1, 2]);        // 80 and 8080 are both a web page
  assert.deepEqual(got["Web page (HTTPS)"], [2]);
  assert.deepEqual(got["SSH"], [1, 4]);
  assert.deepEqual(got["AirPlay"], [3, 4]);
  assert.deepEqual(got["Google Cast"], [3]);
});

test("one device offering a service by port and by mDNS is counted once", () => {
  const page = card([host(1, { openPorts: [631], mdnsServices: "_ipp._tcp,_printer._tcp" })]);
  const [printing] = page.servicesOnDevices();
  assert.equal(printing.label, "Printing (IPP)");
  assert.equal(printing.hosts.size, 1);
  assert.deepEqual([...printing.how].sort(), ["mDNS", "open port"]);
});

test("ports and mDNS types it has no name for are left out", () => {
  const page = card([host(1, { openPorts: [12345, 9999], mdnsServices: "_device-info._tcp,_unknown._udp" })]);
  assert.deepEqual(summary(page.servicesOnDevices()), []);
});

test("devices that are ignored, forgotten, on another site or off the chosen network aren't listed", () => {
  const page = card([
    host(1, { openPorts: [22] }), host(2, { openPorts: [22], ignored: true }), host(3, { openPorts: [22], forgotten: true }),
    host(4, { openPorts: [22], remote: "Cabin" }), host(5, { openPorts: [22] }),
  ], h => h.id !== 5);
  assert.deepEqual(summary(page.servicesOnDevices()), [["SSH", [1]]]);
});

test("the services with the most devices come first, then by name", () => {
  const page = card([
    host(1, { openPorts: [22, 445] }), host(2, { openPorts: [22, 445] }), host(3, { openPorts: [22] }), host(4, { openPorts: [21] }),
  ]);
  assert.deepEqual(Array.from(page.servicesOnDevices(), s => s.label), ["SSH", "File sharing (SMB)", "FTP"]);
});

test("every name in the card's tables reads as a name, and each port is one the port scan checks", () => {
  const page = card([]);
  for (const label of [...Object.values(page.PORT_SERVICES), ...Object.values(page.MDNS_SERVICES)]) assert.ok(label.length > 1 && !label.includes("_"), `"${label}" should read as a name, not a service type`);
  const scanned = [80, 443, 22, 21, 23, 445, 139, 3389, 5900, 8080, 8443, 53, 548, 631, 9100, 32400, 1883, 5000,
    554, 7000, 8009, 1400, 8123, 1880, 8096, 8200, 2049, 3306, 5432, 6379, 27017, 8883, 1194, 8291];   // PortChecker's common set
  for (const p of Object.keys(page.PORT_SERVICES)) assert.ok(scanned.includes(Number(p)), `port ${p} isn't one the scan checks`);
});

const conn = (id, group, name, state, detail = "", section = "system", at = null) => ({ id, group, name, state, detail, section, at });

test("connections show what's on, in the order of their groups, with how long ago and a link to its settings", () => {
  const page = card([]);
  page.conns = { items: [
    conn("backup", "Sends to", "Nightly backup", "ok", "nightly-1.db (1 MB)", "system", "2026-10-01T03:00:00Z"),
    conn("router", "Reads from", "UniFi controller at 10.0.0.1", "error", "It refused the password", "network"),
  ] };
  const html = page.connectionsHtml();
  assert.ok(html.indexOf("Reads from") < html.indexOf("Sends to"));
  assert.match(html, /href="#settings\/network"[^>]*>UniFi controller at 10.0.0.1/);
  assert.match(html, /It refused the password/);
  assert.match(html, /3 min ago/);
  assert.match(html, /wan-dot down/);          // an error shows red
  assert.match(html, /wan-dot up/);            // working shows green
});

test("what isn't set up is one quiet line, not a row each, with a link to where to set it up", () => {
  const page = card([]);
  page.conns = { items: [conn("mqtt", "Sends to", "MQTT (Home Assistant)", "off", "Publishes sensors.", "system"), conn("webhook", "Sends to", "Alert webhook (Discord, Slack, ntfy and others)", "off", "", "alerts")] };
  const html = page.connectionsHtml();
  assert.match(html, /Not set up:/);
  assert.match(html, /href="#settings\/system"[^>]*>MQTT</);                  // the bracketed part of the name is dropped
  assert.match(html, /href="#settings\/alerts"[^>]*>Alert webhook</);
  assert.doesNotMatch(html, /svc-conn/);
});

test("nothing is drawn before the connections have arrived", () => {
  assert.equal(card([]).connectionsHtml(), "");
});

test("a name or detail with markup in it can't inject any", () => {
  const page = card([]);
  page.conns = { items: [conn("x", "Reads from", "<img src=x onerror=alert(1)>", "ok", "<script>no</script>")] };
  const html = page.connectionsHtml();
  assert.doesNotMatch(html, /<img|<script/);
});
