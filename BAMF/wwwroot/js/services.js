// ---- Network services ----
// What BAMF has seen providing a service to the network, gathered from what it already knows: the public
// address (learned by the speed test and the GreyNoise check), each network's gateway, the DHCP and DNS
// servers the traffic monitor has heard, a router that answers UPnP, and which devices have a service
// open (ports found by a scan) or announce one (mDNS). Nothing here asks the network anything.
const PORT_SERVICES = {
  80: "Web page (HTTP)", 8080: "Web page (HTTP)", 443: "Web page (HTTPS)", 8443: "Web page (HTTPS)", 5000: "Web page (HTTP)",
  22: "SSH", 21: "FTP", 23: "Telnet", 445: "File sharing (SMB)", 139: "File sharing (SMB)", 548: "File sharing (AFP)",
  3389: "Remote Desktop", 5900: "VNC", 53: "DNS", 631: "Printing (IPP)", 9100: "Printing (raw)", 32400: "Plex", 1883: "MQTT",
  554: "Camera stream (RTSP)", 7000: "AirPlay", 8009: "Google Cast", 1400: "Sonos", 8123: "Home Assistant", 1880: "Node-RED",
  8096: "Jellyfin", 8200: "Media server (DLNA)", 2049: "File sharing (NFS)", 3306: "MySQL", 5432: "PostgreSQL", 6379: "Redis",
  27017: "MongoDB", 8883: "MQTT (TLS)", 1194: "OpenVPN", 8291: "Winbox (MikroTik)",
};
const MDNS_SERVICES = {
  "_airplay._tcp": "AirPlay", "_raop._tcp": "AirPlay audio", "_googlecast._tcp": "Google Cast", "_spotify-connect._tcp": "Spotify Connect",
  "_sonos._tcp": "Sonos", "_amzn-wplay._tcp": "Fire TV", "_androidtvremote2._tcp": "Android TV remote", "_hap._tcp": "HomeKit",
  "_matter._tcp": "Matter", "_matterc._udp": "Matter", "_hue._tcp": "Philips Hue", "_homeassistant._tcp": "Home Assistant",
  "_esphomelib._tcp": "ESPHome", "_octoprint._tcp": "OctoPrint", "_ipp._tcp": "Printing (IPP)", "_ipps._tcp": "Printing (IPP)",
  "_printer._tcp": "Printing (LPD)", "_pdl-datastream._tcp": "Printing (raw)", "_scanner._tcp": "Scanning", "_uscan._tcp": "Scanning",
  "_smb._tcp": "File sharing (SMB)", "_afpovertcp._tcp": "File sharing (AFP)", "_nfs._tcp": "File sharing (NFS)", "_adisk._tcp": "Time Machine",
  "_ssh._tcp": "SSH", "_sftp-ssh._tcp": "SSH", "_http._tcp": "Web page (HTTP)", "_https._tcp": "Web page (HTTPS)", "_mqtt._tcp": "MQTT",
  "_daap._tcp": "iTunes sharing", "_plexmediasvr._tcp": "Plex", "_home-assistant._tcp": "Home Assistant", "_homekit._tcp": "HomeKit",
  "_hap._udp": "HomeKit", "_airport._tcp": "AirPort", "_mediaremotetv._tcp": "Apple TV remote", "_appletv-v2._tcp": "Apple TV remote",
  "_dacp._tcp": "iTunes remote", "_rfb._tcp": "Screen sharing (VNC)", "_telnet._tcp": "Telnet", "_ftp._tcp": "FTP", "_webdav._tcp": "WebDAV",
  "_roku-rcp._tcp": "Roku", "_wled._tcp": "WLED", "_arduino._tcp": "Arduino updates", "_prometheus-http._tcp": "Prometheus",
  "_ipp-tls._tcp": "Printing (IPP)", "_nvstream_dbd._tcp": "Game streaming", "_steam-streaming._tcp": "Game streaming",
  "_ewelink._tcp": "eWeLink", "_shelly._tcp": "Shelly", "_lutron._tcp": "Lutron", "_apple-mobdev2._tcp": "Apple sync",
  "_sleep-proxy._udp": "Sleep proxy", "_psia._tcp": "Camera (PSIA)", "_axis-video._tcp": "Camera (Axis)", "_rtsp._tcp": "Camera stream (RTSP)",
};
const SERVICES_SHOWN = 12;
let servicesAll = false;

// BAMF's own connections: what it reads from, sends to and calls out to, and whether each is working.
let connectionsCache = null;
function loadConnections() {
  return fetch("/api/connections").then(r => r.ok ? r.json() : null).then(c => { if (c) connectionsCache = c; return c; }).catch(() => null);
}
const svcBlock = (title, inner) => `<div class="svc-block"><div class="svc-h">${esc(title)}</div>${inner}</div>`;
const CONN_GROUPS = ["Reads from", "Sends to", "Calls out to the internet"];
const CONN_STATE_WORDS = { ok: "Working", warn: "Needs a look", error: "Not working", waiting: "On, nothing yet", off: "Off" };
function connectionsHtml() {
  const items = (connectionsCache && connectionsCache.items) || [];
  if (!items.length) return "";
  const link = (it, text) => `<a href="#settings/${esc(it.section)}" class="svc-link">${esc(text)}</a>`;
  const out = [`<div class="svc-part"><h4>BAMF's own connections</h4><div class="sub">What BAMF reads from, sends to and calls out to, and whether each is working.</div>`];
  for (const g of CONN_GROUPS) {
    const mine = items.filter(i => i.group === g), on = mine.filter(i => i.state !== "off"), off = mine.filter(i => i.state === "off");
    if (!mine.length) continue;
    const rows = on.map(i => `<div class="svc-conn"><span class="wan-dot ${i.state === "ok" ? "up" : i.state === "error" ? "down" : i.state === "warn" ? "slow" : ""}" title="${esc(CONN_STATE_WORDS[i.state] || i.state)}"></span>` +
      `<span class="svc-cn"><b>${link(i, i.name)}</b><span class="svc-cd">${esc(i.detail)}</span></span>` +
      `<span class="svc-ca">${i.at ? esc(fmtAgo(i.at)) : ""}</span></div>`).join("");
    const notSet = off.length ? `<div class="watch-note" style="margin-top:4px">Not set up: ${off.map(i => `<a href="#settings/${esc(i.section)}" class="svc-link" title="${esc(i.detail)}">${esc(i.name.replace(/ \(.*$/, ""))}</a>`).join(" · ")}</div>` : "";
    out.push(svcBlock(g, rows + notSet));
  }
  out.push("</div>");
  return out.join("");
}

// Who offers each service: label -> { how: Set of "open port"/"mDNS", hosts: Map(id -> host) }.
function servicesOnDevices() {
  const out = new Map();
  const add = (label, h, how) => {
    if (!out.has(label)) out.set(label, { label, hosts: new Map(), how: new Set() });
    const e = out.get(label); e.hosts.set(h.id, h); e.how.add(how);
  };
  for (const h of hosts) {
    if (h.remote || h.ignored || h.forgotten || !inNetwork(h)) continue;
    for (const p of h.openPorts || []) if (PORT_SERVICES[p]) add(PORT_SERVICES[p], h, "open port");
    for (const s of String(h.mdnsServices || "").split(",")) if (MDNS_SERVICES[s.trim().toLowerCase()]) add(MDNS_SERVICES[s.trim().toLowerCase()], h, "mDNS");
  }
  return [...out.values()].sort((a, b) => b.hosts.size - a.hosts.size || a.label.localeCompare(b.label));
}

// The public address: what BAMF last learned, where from and how long it has been that, with a button to
// ask again now. Learned from the speed test, the daily GreyNoise check, or this button.
const IP_SOURCES = { speedtest: "the speed test", greynoise: "the GreyNoise check", lookup: "a lookup" };
function externalIpHtml(x) {
  const ask = label => `<button type="button" class="toggle" id="svcLookup" title="Asks Cloudflare which address this network appears from: one request for a file of no bytes">${label}</button>`;
  if (!x) return `<div class="svc-row">${ask("Look it up")}</div><div class="watch-note" style="margin-top:4px">Not known yet. BAMF learns it from the speed test (Settings → Internet), the daily GreyNoise check (Settings → Security) or the button above, which asks Cloudflare once.</div>`;
  return `<div class="svc-row"><span class="svc-ip mono" id="svcIp">${esc(x.ip)}</span><button type="button" class="toggle" id="svcCopy" title="Copy this address">Copy</button>${ask("Check now")}` +
    `<span class="meta">from ${esc(IP_SOURCES[x.source] || x.source)}, ${esc(fmtAgo(x.at))}</span></div>` +
    `<div class="watch-note" style="margin-top:2px">${x.previous
      ? `Changed ${esc(fmtAgo(x.changedAt))}; it was ${esc(x.previous)}.`
      : `The same address since ${esc(fmtAgo(x.since).replace(" ago", ""))} ago, as far as BAMF has seen.`}</div>`;
}

function renderServices() {
  const body = $("servicesBody");
  if (!body) return;
  const t = trafficCache || {}, st = t.status || {}, sec = securityCache || {}, wan = wanCache || {};
  const block = svcBlock;
  const note = text => `<div class="watch-note" style="margin-top:0">${text}</div>`;
  const trust = (kind, ip, trusted) => `<button type="button" class="toggle" data-kind="${kind}" data-ip="${esc(ip)}" data-trusted="${trusted ? 0 : 1}" title="${trusted ? "Forget this server, so it alerts if seen again" : "Trust this server, so it never alerts"}">${trusted ? "Forget" : "Trust"}</button>`;
  const html = [];

  // The public address.
  const x = wan.externalIp;
  html.push(block("Internet address", externalIpHtml(x)));

  // Each network's gateway.
  const places = Object.entries(networkPlaces || {}).filter(([cidr]) => network === "all" || network === cidr).sort((a, b) => a[0].localeCompare(b[0], undefined, { numeric: true }));
  const gws = places.filter(([, p]) => p && p.gateway).map(([cidr, p]) => {
    const h = hosts.find(y => y.ip === p.gateway && !y.forgotten && !y.remote);
    const ms = h && h.online && h.latencyMs != null ? ` · answers in ${h.latencyMs} ms` : h && !h.online ? " · not answering" : "";
    return `<div class="svc-row"><span class="mono">${esc(p.gateway)}</span><span class="meta">${esc(h ? dispName(h) + " · " : "")}${esc(cidr)}${esc(ms)}</span></div>`;
  });
  html.push(block("Gateway", gws.join("") || note("No gateway known.")));

  // DHCP and DNS, from the traffic monitor.
  const watching = st.available && st.enabled && st.running;
  const why = !st.available ? "The traffic monitor needs the Npcap driver, which isn't installed."
    : !st.enabled ? "Off. Turn on the traffic monitor in Settings → Scanning and BAMF will hear who hands out addresses and answers DNS."
    : !st.running ? "The traffic monitor isn't running" + (st.error ? ": " + st.error : ".") : "";
  html.push(block("DHCP", ((t.dhcp || []).map(d => `<div class="svc-row"><span class="mono">${esc(d.ip)}</span><span class="meta">${esc(d.name && d.name !== d.mac ? d.name + " · " : "")}${esc(d.mac)} · ${d.offers} offer${d.offers === 1 ? "" : "s"} · last ${esc(fmtAgo(d.lastSeen))}</span>${trust("dhcp", d.ip, d.trusted)}</div>`).join(""))
    || note(watching ? "No DHCP offer heard yet. One turns up the next time a device asks for an address." : why)));
  html.push(block("DNS", ((t.dns || []).map(d => `<div class="svc-row"><span class="mono">${esc(d.ip)}</span><span class="meta">${esc(d.name ? d.name + " · " : "")}${d.clients} device${d.clients === 1 ? "" : "s"} · ${Number(d.queries).toLocaleString()} queries · last ${esc(fmtAgo(d.lastSeen))}</span>${trust("dns", d.ip, d.trusted)}</div>`).join(""))
    || note(watching ? "No DNS query heard yet." : why)));

  // UPnP, from the last health check.
  const up = sec.upnp;
  html.push(block("UPnP", up && (up.devices || []).length
    ? up.devices.map(u => { const h = hosts.find(y => y.id === u.hostId); return `<div class="svc-row"><span class="mono">${esc(u.ip)}</span><span class="meta">${esc(h ? dispName(h) + " · " : "")}${esc(u.server || "answers UPnP")} · any device can open ports through it</span></div>`; }).join("")
    : up ? note(`No router answered a UPnP search (${esc(fmtAgo(up.checkedAt))}).`)
    : note("Not checked yet. Scan → Health check asks your router.")));

  // mDNS, from the names and services devices announce.
  const mdns = hosts.filter(h => !h.remote && !h.ignored && !h.forgotten && inNetwork(h) && (h.mdnsName || h.mdnsServices));
  html.push(block("mDNS (Bonjour)", mdns.length
    ? note(`${mdns.length} device${mdns.length === 1 ? "" : "s"} announce${mdns.length === 1 ? "s" : ""} a name or services this way. BAMF listens, and uses what it hears to name and recognise them.`)
    : note("Nothing heard yet. It's on under Settings → Scanning; devices announce themselves every so often.")));

  // What the devices offer.
  const svc = servicesOnDevices(), shown = servicesAll ? svc : svc.slice(0, SERVICES_SHOWN);
  html.push(block("On your devices", svc.length
    ? shown.map(s => {
        const list = [...s.hosts.values()].sort((a, b) => dispName(a).localeCompare(dispName(b)));
        const chips = list.slice(0, 6).map(h => `<button type="button" class="svc-chip${h.online ? "" : " off"}" data-id="${h.id}" title="${esc(h.ip)}">${esc(dispName(h))}</button>`).join("");
        return `<div class="svc-row top"><span class="svc-name">${esc(s.label)}</span><span class="svc-count" title="${esc([...s.how].join(" and "))}">${list.length}</span><span class="svc-chips">${chips}${list.length > 6 ? `<span class="meta">+${list.length - 6} more</span>` : ""}</span></div>`;
      }).join("") + (svc.length > SERVICES_SHOWN ? `<button type="button" class="toggle" id="svcMore" style="margin-top:6px">${servicesAll ? "Show fewer" : `Show all ${svc.length}`}</button>` : "")
    : note("None seen. Ports found open by a scan, and services devices announce over mDNS, are listed here. Scan → Health check scans the common ports of every device that's online.")));

  html.push(connectionsHtml());
  $("servicesSub").textContent = network === "all" ? "What BAMF has seen providing a service on your networks." : `What BAMF has seen providing a service on ${network}.`;
  body.innerHTML = html.join("");

  body.querySelectorAll("button[data-kind]").forEach(b => b.onclick = async () => {
    await fetch("/api/traffic/trust", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ kind: b.dataset.kind, ip: b.dataset.ip, trusted: b.dataset.trusted === "1" }) });
    toast(b.dataset.trusted === "1" ? `${esc(b.dataset.ip)} is trusted: it won't alert` : `${esc(b.dataset.ip)} forgotten: it alerts if seen again`);
    try { trafficCache = await loadTraffic(); } catch { }
    renderTrafficCards();
  });
  body.querySelectorAll(".svc-chip").forEach(b => b.onclick = () => jumpToHost(Number(b.dataset.id)));
  const more = $("svcMore"); if (more) more.onclick = () => { servicesAll = !servicesAll; renderServices(); };
  const look = $("svcLookup");
  if (look) look.onclick = async () => {
    look.disabled = true; look.textContent = "Looking…";
    try {
      const r = await fetch("/api/externalip/lookup", { method: "POST" });
      const d = await r.json().catch(() => ({}));
      if (!r.ok) toast(esc(d.error || "Couldn't look it up."));
      else {
        if (wanCache) wanCache.externalIp = d.externalIp; else await loadWan(true);
        toast(d.changed ? `Your address changed: it's now ${esc(d.externalIp.ip)}` : `Your address is ${esc(d.externalIp.ip)}`);
      }
    } catch { toast("Couldn't reach BAMF."); }
    renderServices();
  };
  const copy = $("svcCopy");
  if (copy) copy.onclick = async () => {
    try { await navigator.clipboard.writeText(x.ip); toast("Copied " + esc(x.ip)); } catch { toast("Couldn't copy: select the address and copy it"); }
  };
}
