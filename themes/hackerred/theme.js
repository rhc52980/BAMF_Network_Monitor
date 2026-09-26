// Hacker Red: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Hacker Red: every so often the header glitches.
function buildHackerRed() {
  if (calmMotion()) return;
  const glitch = () => {
    const hd = document.querySelector("header");
    if (hd && !document.hidden) {
      hd.classList.add("hr-glitch");
      festiveTimers.push(setTimeout(() => hd.classList.remove("hr-glitch"), 450));
    }
    festiveTimers.push(setTimeout(glitch, rnd(20e3, 45e3)));
  };
  festiveTimers.push(setTimeout(glitch, rnd(4e3, 10e3)));
  festiveStops.push(() => document.querySelector("header")?.classList.remove("hr-glitch"));
}

BAMF.registerTheme("hackerred", ctx => buildHackerRed(ctx.root, ctx.switched));
})();
