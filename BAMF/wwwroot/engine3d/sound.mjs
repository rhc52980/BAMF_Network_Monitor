// A little sound for the 3D tab, off until it's switched on (a click is what lets a page make any): a low hum, and a soft
// blip for what happens. Made with the browser's own audio, so there are no files and nothing is fetched. Everything is
// quiet on purpose; this is for a wall display, not an alarm (the alerts are that).

const CUES = {
  // [frequency steps (Hz), seconds per step, wave, level]
  arrive: [[520, 780], 0.09, "sine", 0.5],
  leave: [[620, 410], 0.1, "sine", 0.4],
  drop: [[330, 220, 150], 0.12, "triangle", 0.55],        // a device that was on has gone off
  back: [[300, 450, 600], 0.08, "sine", 0.5],
  unusual: [[740, 520, 740, 520], 0.1, "square", 0.22],
  service: [[180, 130], 0.2, "sawtooth", 0.3],
  recover: [[400, 600], 0.1, "sine", 0.4],
  scan: [[900, 1100, 900, 1100, 900], 0.045, "square", 0.16],
  destination: [[880, 1320], 0.08, "sine", 0.4],
  internet: [[220, 160, 110], 0.25, "sawtooth", 0.35],
};

export function createSound() {
  let ctx = null, master = null, hum = null, on = false;
  const last = new Map();

  function ensure() {
    if (ctx) return ctx;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    ctx = new AC();
    master = ctx.createGain(); master.gain.value = 0.06; master.connect(ctx.destination);
    return ctx;
  }

  function startHum() {
    if (!ctx || hum) return;
    const g = ctx.createGain(); g.gain.value = 0.0;
    g.gain.linearRampToValueAtTime(0.5, ctx.currentTime + 2);
    const a = ctx.createOscillator(), b = ctx.createOscillator(), lfo = ctx.createOscillator(), lg = ctx.createGain();
    a.type = "sine"; a.frequency.value = 55; b.type = "sine"; b.frequency.value = 82.6;
    lfo.frequency.value = 0.11; lg.gain.value = 0.18;
    lfo.connect(lg); lg.connect(g.gain);
    a.connect(g); b.connect(g); g.connect(master);
    a.start(); b.start(); lfo.start();
    hum = { g, nodes: [a, b, lfo] };
  }
  function stopHum() {
    if (!hum) return;
    const h = hum; hum = null;
    try { h.g.gain.linearRampToValueAtTime(0, ctx.currentTime + 0.4); } catch { /* closing */ }
    setTimeout(() => h.nodes.forEach(n => { try { n.stop(); } catch { /* already */ } }), 500);
  }

  return {
    get on() { return on; },
    /** Must be called from a click or key press, which is what lets the page make a sound. */
    async enable() {
      const c = ensure();
      if (!c) return false;
      try { await c.resume(); } catch { /* it tries again on the next click */ }
      on = true; startHum();
      return true;
    },
    disable() { on = false; stopHum(); },
    /** One cue by name; the same cue is held off for a moment so a burst of arrivals is a few blips, not a chord. */
    cue(name) {
      if (!on || !ctx || ctx.state === "closed") return;
      const spec = CUES[name]; if (!spec) return;
      const now = ctx.currentTime;
      if ((last.get(name) ?? -9) > now - 0.35) return;
      last.set(name, now);
      const [steps, each, wave, level] = spec;
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.type = wave;
      steps.forEach((f, i) => o.frequency.setValueAtTime(f, now + i * each));
      g.gain.setValueAtTime(0, now);
      g.gain.linearRampToValueAtTime(level, now + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, now + steps.length * each + 0.12);
      o.connect(g); g.connect(master);
      o.start(now); o.stop(now + steps.length * each + 0.2);
    },
    dispose() { on = false; stopHum(); if (ctx) { try { ctx.close(); } catch { /* ok */ } ctx = null; } },
  };
}
