// ---- The internet watch ----
let wanCache = null, wanAt = 0, wanAllOutages = false;
function loadWan(force) {
  if (!force && wanCache && Date.now() - wanAt < 30e3) return Promise.resolve(wanCache);
  return fetch("/api/wan").then(r => r.ok ? r.json() : null).then(d => {
    if (d) { wanCache = d; wanAt = Date.now(); }
    return wanCache;
  }).catch(() => wanCache);
}
const wanClock = d => d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
function renderWanCard() {
  const card = $("wanCard");
  if (!card) return;
  const d = wanCache, st = d && d.state;
  if (!st || !st.enabled) {
    $("wanSub").textContent = "Off. Switch on \u201cWatch the internet connection\u201d under Settings \u2192 Internet and BAMF will keep an eye on the line out of the house.";
    $("wanBody").innerHTML = "";
    return;
  }
  $("wanSub").textContent = `Pinging ${esc(st.target)} and your router once a minute.`;
  const bits = [];
  const state = st.up === null ? ["", "Waiting for the first ping\u2026", ""]
    : st.up && st.slow ? ["slow", "The internet is slow", (st.ms != null ? st.ms + " ms to " + st.target : "") + (st.usualMs ? ` \u00b7 usually ${st.usualMs} ms` : "")]
    : st.up ? ["up", "The internet is up", st.ms != null ? st.ms + " ms to " + st.target : ""]
    : ["down", st.routerUp ? "The internet is down" : "The internet is down, and so is your router",
       st.routerUp ? "Your router is answering, so it's the line out of the house" : "Your router isn't answering either, so it's in here"];
  bits.push(`<div class="wan-state"><span class="wan-dot ${state[0]}"></span><span class="wan-now">${esc(state[1])}</span><span class="wan-ms">${esc(state[2])}</span></div>`);
  // The last hour's quality: what share of pings went unanswered, how much the time varied, how long a DNS lookup took.
  const q = d.quality, probs = d.qualityProblems || [];
  if (q && q.samples >= 2) {
    const parts = [`${q.lossPercent}% of pings lost`];
    if (q.jitterMs != null) parts.push(`jitter ${q.jitterMs} ms`);
    if (q.dnsMs != null) parts.push(`DNS ${q.dnsMs} ms`);
    else if (q.dnsMeasured) parts.push("DNS not answering");
    const bad = probs.length ? ` <span class="bad">${esc(probs.map(p => p === "loss" ? "dropping packets" : p === "jitter" ? "jittery" : "DNS slow").join(", "))} now</span>` : "";
    bits.push(`<div class="sub wan-quality">Last hour: ${esc(parts.join(" · "))}${bad}${d.qualityAlert ? "" : " · quality alerts off"}</div>`);
  }
  // A day of samples, one bar a minute, with gaps where BAMF wasn't watching.
  const samples = (d.samples || []).slice(-1440);
  if (samples.length) {
    // One bar per few minutes, showing the worst of what happened in them, so
    // a short outage is never averaged into looking fine.
    //
    // How many bars is measured, not fixed. Each wants its 2px and the 1px gap
    // beside it, and flex won't shrink them below that, so a fixed 160 needed
    // 479px of card. The card is often half that, and the overflow painted the
    // rest of the day straight across the panel next door.
    const wide = $("wanBody").clientWidth || 340;
    const COLS = Math.max(40, Math.min(240, Math.floor((wide + 1) / 3)));
    const span = 24 * 3600e3 / COLS, end = Date.now();
    const buckets = Array.from({ length: COLS }, () => ({ n: 0, ms: 0, down: 0, local: 0, slow: 0, worst: 0 }));
    const limit = st.slowMs || 0;
    for (const s of samples) {
      const i = Math.floor((new Date(s.at) - (end - 24 * 3600e3)) / span);
      if (i < 0 || i >= COLS) continue;
      const b = buckets[i];
      b.n++;
      if (s.internet >= 0) {
        b.ms += s.internet;
        if (limit && s.internet >= limit) b.slow++;
        if (s.internet > b.worst) b.worst = s.internet;
      }
      else if (s.gateway >= 0) b.down++;
      else b.local++;
    }
    let strip = "";
    for (let i = 0; i < COLS; i++) {
      const b = buckets[i], at = new Date(end - 24 * 3600e3 + i * span + span / 2);
      const cls = !b.n ? "gap" : b.local ? "local" : b.down ? "down" : b.slow ? "slow" : "";
      const up = b.n - b.down - b.local;
      const title = !b.n ? `${wanClock(at)} \u00b7 BAMF wasn't watching`
        : b.local ? `${wanClock(at)} \u00b7 no answer, router silent too`
        : b.down ? `${wanClock(at)} \u00b7 ${b.down} minute${b.down === 1 ? "" : "s"} with no answer`
        : b.slow ? `${wanClock(at)} \u00b7 slow, ${b.worst} ms at worst`
        : `${wanClock(at)} \u00b7 ${Math.round(b.ms / Math.max(1, up))} ms`;
      strip += `<i class="${cls}" title="${esc(title)}"></i>`;
    }
    bits.push(`<div class="wan-strip">${strip}</div>`);
    bits.push(`<div class="wan-scale"><span>${esc(wanClock(new Date(end - 24 * 3600e3)))}</span><span>last 24 hours</span><span>now</span></div>`);
  }
  const out = (d.outages || []).concat((d.slow || []).map(o => Object.assign({ isSlow: true }, o)))
    .sort((a, b) => (b.running - a.running) || String(b.start).localeCompare(String(a.start)));
  const slowCount = (d.slow || []).length;
  if (out.length) {
    const shown = wanAllOutages ? out : out.slice(0, 6);
    bits.push(`<div class="sub" style="margin-top:10px">${slowCount ? "Outages and slow spells" : "Outages"}, newest first \u00b7 kept even after the readings are pruned</div>`);
    for (const o of shown) {
      const when = `${fmtExact(o.start)} \u2192 ${wanClock(new Date(o.end))}`;
      if (o.isSlow) {
        bits.push(`<div class="wan-outage slow${o.running ? " running" : ""}">` +
          `<span>${esc(o.running ? fmtExact(o.start) + " \u00b7 still slow" : when)} \u00b7 slow, ${o.worst} ms at worst${o.local ? " \u00b7 router slow too" : ""}</span>` +
          `<span class="dur">${o.minutes} min</span></div>`);
        continue;
      }
      bits.push(`<div class="wan-outage${o.local ? " local" : ""}${o.running ? " running" : ""}">` +
        `<span>${esc(o.running ? fmtExact(o.start) + " \u00b7 still down" : when)}${o.local ? " \u00b7 router down too" : ""}</span>` +
        `<span class="dur">${o.minutes} min</span></div>`);
    }
    if (out.length > 6) bits.push(`<button type="button" class="hy-more" id="wanMore">${wanAllOutages ? "Show fewer" : `All ${out.length}`}</button>`);
  } else if (samples.length) {
    bits.push(`<div class="sub" style="margin-top:8px">No outages recorded yet.</div>`);
  }
  bits.push(`<div class="sub" style="margin-top:10px"><a href="/report/internet" target="_blank" rel="noopener" id="wanReport" class="svc-link" style="color:var(--focus)">Report for my provider</a> · the last 30 days on one printable page</div>`);
  $("wanBody").innerHTML = bits.join("");
  const more = $("wanMore");
  if (more) more.onclick = () => { wanAllOutages = !wanAllOutages; renderWanCard(); };
}

// The bar count comes from the card's own width, so a resized window wants a
// re-bucket. Only while Activity is the tab being looked at.
let wanResize = null;
window.addEventListener("resize", () => {
  if (view !== "activity" || !wanCache) return;
  clearTimeout(wanResize);
  wanResize = setTimeout(renderWanCard, 200);
});

function renderWanToggle() {
  const t = $("setWan");
  if (!t) return;
  const st = wanCache && wanCache.state;
  const on = !!(st && st.enabled);
  setToggleState(t, on);
  t.title = on ? "BAMF is watching the internet connection - click to stop" : "Click to have BAMF watch the internet connection";
  if (st && st.target) $("setWanTarget").value = st.target;
  if (wanCache && typeof wanCache.qualityAlert === "boolean") wanQualityAlert = wanCache.qualityAlert;
  renderWanQualityToggle();
  if (settingsData && settingsData.editable && settingsData.editable.wanInterval) {
    const secs = settingsData.editable.wanInterval;
    $("setWanInterval").value = secs;
    $("setWanIntervalStatus").textContent = on ? `Every ${secs} seconds. An outage is called after ${Math.round(secs * 3 / 60) || 1} minute${Math.round(secs * 3 / 60) === 1 ? "" : "s"} of no answer.` : "";
  }
  $("setWanStatus").textContent = !on ? "" : st.up === null ? "On. Waiting for the first ping."
    : st.up ? `On. The internet is up, ${st.ms} ms to ${st.target}.` : "On. The internet is down.";
  renderWanSlow();
}
function renderWanSlow(keepInputs) {
  const st = wanCache && wanCache.state;
  if (!st || !$("setWanSlowMode")) return;
  const mode = st.slowMode || "auto", fixed = /^\d+$/.test(mode);
  if (!keepInputs) {
    $("setWanSlowMode").value = fixed ? "fixed" : mode;
    if (fixed) $("setWanSlowMs").value = mode;
  }
  const showMs = $("setWanSlowMode").value === "fixed";
  $("setWanSlowMs").hidden = !showMs;
  $("setWanSlowUnit").hidden = !showMs;
  const usual = st.usualMs ? `Your usual is ${st.usualMs} ms.` : "";
  $("setWanSlowStatus").textContent = !st.enabled ? ""
    : mode === "off" ? "Not watching for slowness."
    : fixed ? `Slow above ${mode} ms. ${usual}`.trim()
    : st.slowMs ? `${usual} Slow above ${st.slowMs} ms.`
    : "It learns your usual ping over the first half hour or so, then starts watching.";
}
// Internet quality alerts: lost pings, jitter and slow DNS, from the watch's own readings.
function renderWanQualityToggle() {
  const t = $("setWanQuality");
  if (!t) return;
  setToggleState(t, wanQualityAlert);
  t.title = wanQualityAlert ? "Lost pings, jitter and slow DNS are reported - click to stop" : "Click to be told about lost pings, jitter and slow DNS";
}
$("setWanQuality").onclick = async () => {
  const next = !wanQualityAlert;
  try {
    const r = await fetch("/api/settings/wan-quality", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }) });
    if (!r.ok) throw new Error("HTTP " + r.status);
    wanQualityAlert = (await r.json()).enabled;
    renderWanQualityToggle();
    toast(wanQualityAlert ? "Lost pings, jitter and slow DNS will be reported" : "Internet quality alerts are off");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};
$("setWanSlowMode").onchange = () => renderWanSlow(true);
$("setWanSlowSave").onclick = async () => {
  const mode = $("setWanSlowMode").value, ms = Math.round(Number($("setWanSlowMs").value));
  try {
    const r = await fetch("/api/settings/wanslow", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ mode, ms }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't save that"); return; }
    await loadWan(true);
    renderWanToggle();
    renderWanCard();
    toast(mode === "off" ? "BAMF won't watch for a slow internet" : mode === "fixed" ? `The internet is called slow above ${ms} ms` : "The internet is called slow at four times its usual ping");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};
$("setWan").onclick = async () => {
  const on = !!(wanCache && wanCache.state && wanCache.state.enabled);
  try {
    const r = await fetch("/api/settings/wanwatch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: !on }) });
    if (!r.ok) throw new Error("HTTP " + r.status);
    await loadWan(true);
    renderWanToggle();
    renderWanCard();
    toast(on ? "The internet watch is off: BAMF won't ping anything outside" : "The internet watch is on: a ping a minute to " + (wanCache.state.target || "8.8.8.8"));
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); renderWanToggle(); }
};
$("setWanIntervalSave").onclick = async () => {
  const seconds = Math.round(Number($("setWanInterval").value));
  try {
    const r = await fetch("/api/settings/waninterval", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ seconds }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't save that"); return; }
    $("setWanInterval").value = d.seconds;
    $("setWanIntervalStatus").textContent = `Every ${d.seconds} seconds. An outage is called after ${Math.round(d.seconds * 3 / 60) || 1} minute${Math.round(d.seconds * 3 / 60) === 1 ? "" : "s"} of no answer.`;
    toast(`The internet watch now checks every ${d.seconds} seconds`);
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};
$("setWanTargetSave").onclick = async () => {
  const target = $("setWanTarget").value.trim();
  try {
    const r = await fetch("/api/settings/wantarget", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ target }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't save that address"); return; }
    await loadWan(true);
    renderWanToggle();
    renderWanCard();
    toast("The internet watch now pings " + target);
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};

// ---- The speed test ----
let speedCache = null, speedPoll = null;
function loadSpeed() {
  return fetch("/api/speedtest").then(r => r.ok ? r.json() : null).then(d => (speedCache = d || speedCache)).catch(() => speedCache);
}
const spdNum = v => v >= 100 ? Math.round(v) : Math.round(v * 10) / 10;
const spdWhen = iso => new Date(iso).toLocaleString([], { weekday: "short", hour: "numeric", minute: "2-digit" });
// While a test runs, ask how it's going every second, and draw it; when it's
// done, one more look for the result.
function watchSpeed() {
  if (speedPoll) return;
  speedPoll = setInterval(async () => {
    await loadSpeed();
    renderSpeed();
    renderSpeedSetting();
    if (!speedCache || !speedCache.running) {
      clearInterval(speedPoll); speedPoll = null;
      const r = speedCache && speedCache.results && speedCache.results[speedCache.results.length - 1];
      if (r) toast(r.error ? "The speed test didn't finish: " + r.error : `${spdNum(r.down)} Mbps down, ${spdNum(r.up)} Mbps up, ${r.ping} ms`);
    }
  }, 1000);
}
async function runSpeed() {
  try {
    const r = await fetch("/api/speedtest/run", { method: "POST" });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't start the speed test"); return; }
    await loadSpeed();
    renderSpeed();
    renderSpeedSetting();
    watchSpeed();
  } catch (e) { console.error(e); toast("Couldn't start the speed test - see the server log"); }
}
function renderSpeed() {
  const box = $("spdBody");
  if (!box) return;
  const d = speedCache;
  if (!d) { box.innerHTML = ""; return; }
  const results = d.results || [], ok = results.filter(r => !r.error), last = ok[ok.length - 1];
  const lastAny = results[results.length - 1];
  const bits = [`<div class="spd-head"><h4>Speed</h4><button type="button" class="export-btn" id="spdRun"${d.running ? " disabled" : ""}>${d.running ? "Testing…" : "Run now"}</button></div>`];
  if (d.running) {
    const p = d.progress || {};
    const phase = p.phase === "download" ? "Testing download" : p.phase === "upload" ? "Testing upload" : "Timing the round trip";
    bits.push(`<div class="spd-running">${phase}…${p.mbps != null ? " " + spdNum(p.mbps) + " Mbps" : ""}</div>`);
  } else if (last) {
    const u = d.usual;
    const low = (v, usual) => usual && v < usual * .5 ? " bad" : "";
    bits.push(`<div class="spd-now">` +
      `<span><span class="arrow">↓</span><span class="v${low(last.down, u && u.down)}">${spdNum(last.down)}</span><span class="u">Mbps down</span></span>` +
      `<span><span class="arrow">↑</span><span class="v${low(last.up, u && u.up)}">${spdNum(last.up)}</span><span class="u">Mbps up</span></span>` +
      `<span><span class="v">${last.ping}</span><span class="u">ms${last.jitter ? ", ±" + last.jitter : ""}</span></span></div>`);
    const meta = [`${esc(spdWhen(last.at))}${last.manual ? " · run by hand" : ""}`, `Cloudflare ${esc(last.server || "")}`.trim(), `${last.mb} MB`];
    if (u) meta.push(`usually ${spdNum(u.down)} down, ${spdNum(u.up)} up`);
    bits.push(`<div class="spd-meta">${meta.join(" · ")}</div>`);
  }
  if (!d.running && lastAny && lastAny.error)
    bits.push(`<div class="spd-meta"><span class="bad">The last test didn't finish</span> (${esc(spdWhen(lastAny.at))}): ${esc(lastAny.error)}</div>`);
  if (!results.length && !d.running) {
    bits.push(`<div class="sub">No tests yet. <b>Run now</b> tests the line from the machine BAMF runs on, in about half a minute and 125 MB. ` +
      (d.schedule === "off" ? "To test on a schedule, see “Test the internet speed” under Settings → Internet." : "") + `</div>`);
  }
  // A month of tests: a bar for each download, a line through the uploads,
  // and a red tick for any that failed, placed by when they ran.
  const month = results.filter(r => Date.now() - new Date(r.at) < 30 * 864e5);
  if (month.filter(r => !r.error).length >= 2) {
    const W = 300, H = 64, t0 = Date.now() - 30 * 864e5;
    const top = Math.max(1, ...month.map(r => r.down), ...month.map(r => r.up)) * 1.1;
    const x = r => (new Date(r.at) - t0) / (30 * 864e5) * (W - 3);
    const y = v => H - v / top * (H - 2);
    let svg = `<line class="grid" x1="0" x2="${W}" y1="${H - .5}" y2="${H - .5}"/>`;
    for (const r of month) {
      svg += r.error
        ? `<rect class="fail" x="${x(r).toFixed(1)}" y="${H - 6}" width="2.5" height="6"><title>${esc(spdWhen(r.at))} · failed</title></rect>`
        : `<rect class="dn" x="${x(r).toFixed(1)}" y="${y(r.down).toFixed(1)}" width="2.5" height="${(H - y(r.down)).toFixed(1)}"><title>${esc(spdWhen(r.at))} · ${spdNum(r.down)} down, ${spdNum(r.up)} up, ${r.ping} ms</title></rect>`;
    }
    const ups = month.filter(r => !r.error).map(r => `${(x(r) + 1.25).toFixed(1)},${y(r.up).toFixed(1)}`);
    svg += `<polyline class="upl" points="${ups.join(" ")}"/>`;
    bits.push(`<svg class="spd-chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="Speed tests over the last 30 days">${svg}</svg>`);
    bits.push(`<div class="wan-scale"><span>30 days ago</span><span><span class="spd-key"><i></i> down</span> · <span class="spd-key"><i class="up"></i> up</span> · top ${spdNum(top / 1.1)} Mbps</span><span>now</span></div>`);
  }
  box.innerHTML = bits.join("");
  const run = $("spdRun");
  if (run) run.onclick = runSpeed;
  if (d.running) watchSpeed();
}
function renderSpeedSetting() {
  const sel = $("setSpeedSchedule");
  if (!sel || !speedCache) return;
  if (document.activeElement !== sel) sel.value = speedCache.schedule || "off";
  const ok = (speedCache.results || []).filter(r => !r.error), last = ok[ok.length - 1];
  const when = { off: "Not on a schedule.", daily: "Every day at 4:10 am.", "6h": "At 12:10 am, 6:10 am, 12:10 pm and 6:10 pm." }[speedCache.schedule] || "";
  $("setSpeedStatus").textContent = speedCache.running ? "Testing now…"
    : when + (last ? ` Last: ${spdNum(last.down)} Mbps down, ${spdNum(last.up)} up, ${last.ping} ms (${spdWhen(last.at)}).` : "");
  $("setSpeedRun").disabled = !!speedCache.running;
}
$("setSpeedSave").onclick = async () => {
  const schedule = $("setSpeedSchedule").value;
  try {
    const r = await fetch("/api/settings/speedtest", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ schedule }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't save that"); return; }
    await loadSpeed();
    renderSpeedSetting();
    renderSpeed();
    toast(schedule === "off" ? "No scheduled speed tests" : schedule === "daily" ? "A speed test every day at 4:10 am" : "A speed test every six hours");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};
$("setSpeedRun").onclick = runSpeed;

function openScanModal() {
  // populate target dropdown from current networks
  const sel = $("scanTarget");
  sel.innerHTML = '<option value="all">All online hosts</option>';
  const nets = [...new Set(hosts.filter(x => !x.ignored).map(x => x.subnet).filter(Boolean))];
  const cidrKey2 = c => { const [ip,p]=c.split("/"); return ip.split(".").reduce((a,o)=>a*256+(+o||0),0)*100+(+p||0); };
  nets.sort((a,b)=>cidrKey2(a)-cidrKey2(b)).forEach(n => {
    const o = document.createElement("option"); o.value = "net:" + n; o.textContent = n; sel.appendChild(o);
  });
  const ipOpt = document.createElement("option");
  ipOpt.value = "ip"; ipOpt.textContent = "Specific IP address…";
  sel.appendChild(ipOpt);
  $("scanIpRow").style.display = "none";
  $("scanResult").innerHTML = "";
  $("scanModal").hidden = false;
}

// ---- The Scan menu, in the header: every scan you can start by hand. ----
// A sweep looks for devices, the same pass the schedule runs, now instead of
// when it's due; ports and the health check look closer at the ones it found.
let scanOpen = false, sweepWatch = null;
function closeScanMenu() {
  scanOpen = false;
  $("scanMenu").classList.remove("show");
  $("scanBtn").setAttribute("aria-expanded", "false");
}
function buildScanMenu() {
  const nets = (subnets || []).map(n => typeof n === "string" ? n : n.label).filter(Boolean);
  const why = n => scanModes[n] === "paused" ? "paused in Settings" : scanModes[n] === "skipped" ? "no interface on this machine" : "";
  const live = nets.filter(n => !why(n));
  const bits = [`<div class="scan-h">Sweep for devices</div>`];
  bits.push(`<button type="button" data-sweep="*"${live.length ? "" : " disabled"}>${nets.length > 1 ? "Every network now" : "Sweep now"}` +
    `<small>${sweepWatch ? "A sweep is running\u2026" : "The scheduled scan, straight away"}</small></button>`);
  if (nets.length > 1)
    for (const n of nets)
      bits.push(`<button type="button" data-sweep="${esc(n)}"${why(n) ? " disabled" : ""}>${esc(n)}${why(n) ? `<small>${why(n)}</small>` : ""}</button>`);
  bits.push(`<hr><div class="scan-h">Look closer</div>`);
  bits.push(`<button type="button" data-scan="ports">Scan ports\u2026<small>Chosen devices, a network, or one address</small></button>`);
  bits.push(`<button type="button" data-scan="health"${healthRunning ? " disabled" : ""}>Health check` +
    `<small>${healthRunning ? "Running\u2026" : "Ports, UPnP and certificates \u00b7 about a minute"}</small></button>`);
  $("scanMenu").innerHTML = bits.join("");
}
async function sweep(net) {
  try {
    const q = net && net !== "*" ? "?subnet=" + encodeURIComponent(net) : "";
    const r = await fetch("/api/scan" + q, { method: "POST" });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't start a sweep"); return; }
    const nets = d.networks || [];
    toast(nets.length === 1 ? `Sweeping ${nets[0]}\u2026` : `Sweeping ${nets.length} networks\u2026`);
    // Look more often than the usual poll until the sweep lands, so the
    // countdown says "scanning now" and the table fills in as it happens.
    clearInterval(sweepWatch?.timer);
    const from = lastScan, started = Date.now();
    sweepWatch = { timer: setInterval(async () => {
      await refresh();
      const done = lastScan && lastScan !== from;
      if (done || Date.now() - started > 120e3) {
        clearInterval(sweepWatch.timer);
        sweepWatch = null;
        if (done) {
          const shown = hosts.filter(h => !h.ignored && !h.forgotten && !h.remote && (!net || net === "*" || h.subnet === net));
          toast(`Sweep done: ${shown.filter(h => h.online).length} of ${shown.length} online`);
        }
      }
    }, 2000) };
    setTimeout(refresh, 600);
  } catch (e) { console.error(e); toast("Couldn't start a sweep - see the server log"); }
}
$("scanBtn").onclick = e => {
  e.stopPropagation();
  if (toolsOpen) { toolsOpen = false; $("toolsMenu").classList.remove("show"); }
  scanOpen = !scanOpen;
  if (scanOpen) buildScanMenu();
  $("scanMenu").classList.toggle("show", scanOpen);
  $("scanBtn").setAttribute("aria-expanded", String(scanOpen));
};
$("scanMenu").onclick = e => {
  const b = e.target.closest("button");
  if (!b || b.disabled) return;
  e.stopPropagation();
  closeScanMenu();
  if (b.dataset.sweep) sweep(b.dataset.sweep);
  else if (b.dataset.scan === "ports") openScanModal();
  else if (b.dataset.scan === "health") runHealthCheck();
};
document.addEventListener("click", e => { if (scanOpen && !e.target.closest(".scan-wrap")) closeScanMenu(); });

// Find a free address: a fill gauge for each network, its longest runs of
// addresses nothing has used lately, and one suggestion to copy.
async function loadFreeIps() {
  const body = $("freeBody");
  body.innerHTML = `<div class="modal-note">Working it out…</div>`;
  try {
    const r = await fetch("/api/free-ips?days=" + $("freeDays").value);
    if (!r.ok) throw new Error("HTTP " + r.status);
    const nets = await r.json();
    if (!nets.length) { body.innerHTML = `<div class="modal-note">No IPv4 networks between /16 and /30 to look at.</div>`; return; }
    body.innerHTML = nets.map(n => {
      const pct = Math.round(100 * n.used / Math.max(1, n.size));
      const runs = n.runs.map(x => `<li><span class="mono">${esc(x.from)}${x.count > 1 ? " – " + esc(x.to) : ""}</span><span class="n">${x.count}</span></li>`).join("");
      return `<div class="free-net">
        <div class="free-head"><b class="mono">${esc(n.subnet)}</b><span>${n.used} of ${n.size} used · ${n.free} free</span></div>
        <div class="free-gauge ${pct >= 90 ? "full" : pct >= 70 ? "busy" : ""}" title="${pct}% used"><i style="width:${pct}%"></i></div>
        ${n.suggestion ? `<div class="free-pick">Try <b class="mono">${esc(n.suggestion)}</b> <button type="button" class="toggle free-copy" data-ip="${esc(n.suggestion)}">Copy</button></div>` : `<div class="free-pick">No free addresses.</div>`}
        ${runs ? `<ul class="free-runs" title="The longest runs of free addresses">${runs}</ul>` : ""}
      </div>`;
    }).join("");
    body.querySelectorAll(".free-copy").forEach(b => b.onclick = async () => {
      toast(await copyText(b.dataset.ip) ? `Copied ${b.dataset.ip}` : b.dataset.ip);
    });
  } catch (e) { body.innerHTML = `<div class="modal-note">Couldn't work it out: ${esc(e.message)}</div>`; }
}
$("freeOpen").onclick = () => {
  toolsOpen = false; $("toolsMenu").classList.remove("show");
  $("freeModal").hidden = false;
  loadFreeIps();
};
$("freeDays").onchange = loadFreeIps;
$("freeClose").onclick = () => { $("freeModal").hidden = true; };
