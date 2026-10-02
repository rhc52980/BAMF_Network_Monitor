// Which devices the Network services card says offer which service. The card's own code is read from the
// dashboard (js/services.js) and run with the page state it reads made settable. Run from the repo root:
//
//   node --test tests/web/*.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

function card(hosts, inNetwork = () => true) {
  const ctx = vm.createContext({ String, Map, Set });
  ctx.hosts = hosts; ctx.inNetwork = inNetwork;
  vm.runInContext(readFileSync("BAMF/wwwroot/js/services.js", "utf8") + "\nglobalThis.page = { servicesOnDevices, PORT_SERVICES, MDNS_SERVICES };", ctx);
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
  const scanned = [80, 443, 22, 21, 23, 445, 139, 3389, 5900, 8080, 8443, 53, 548, 631, 9100, 32400, 1883, 5000];   // PortChecker's common set
  for (const p of Object.keys(page.PORT_SERVICES)) assert.ok(scanned.includes(Number(p)), `port ${p} isn't one the scan checks`);
});
