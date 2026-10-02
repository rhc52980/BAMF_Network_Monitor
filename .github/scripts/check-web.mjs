// Checks the parts of BAMF a C# build doesn't: the dashboard's scripts, the
// themes, and the JSON files the dashboard reads. Run from the repo root:
//
//   node .github/scripts/check-web.mjs
//
// Exits non-zero, listing every problem, if anything is wrong.
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import vm from "node:vm";

const problems = [];
const fail = (where, what) => problems.push(`${where}: ${what}`);

// A script's syntax, without running it. lineOffset is where it starts in
// its file, so the line reported is the file's.
const syntax = (where, code, lineOffset = 0) => {
  try { new vm.Script(code, { filename: where, lineOffset }); }
  catch (e) {
    const line = /:(\d+)\s*$/.exec(String(e.stack).split("\n")[0]);
    fail(line ? `${where}, line ${line[1]}` : where, e.message);
  }
};

// The scripts inside each page of the dashboard.
const web = "BAMF/wwwroot";
for (const page of readdirSync(web).filter(f => f.endsWith(".html"))) {
  const html = readFileSync(join(web, page), "utf8");
  for (const m of html.matchAll(/<script(\s[^>]*)?>([\s\S]*?)<\/script>/g)) {
    if (/type=["']?importmap/.test(m[1] || "")) {
      // The 3D tab's map of where its library is: valid JSON, pointing at files that are there.
      try {
        for (const target of Object.values(JSON.parse(m[2]).imports)) {
          const rel = target.replace(/^\//, "");
          if (!existsSync(join(web, rel))) fail(page, `the import map points at ${target}, which isn't there`);
        }
      } catch (e) { fail(page, "the import map isn't valid JSON: " + e.message); }
      continue;
    }
    if (/\bsrc=/.test(m[1] || "") || /type=["']?(application\/json|module)/.test(m[1] || "")) continue;
    const start = html.slice(0, m.index + m[0].indexOf(">") + 1).split("\n").length - 1;
    syntax(page, m[2], start);
  }
  // The script files it loads: each there and sound on its own, and all of
  // them together, in order, as the one scope a page's scripts share (a name
  // declared in two of them is an error only then).
  const srcs = [...html.matchAll(/<script\s+src="([^"]+)"><\/script>/g)].map(m => m[1]);
  const all = [];
  for (const src of srcs) {
    const path = join(web, src);
    if (!existsSync(path)) { fail(page, `loads ${src}, which isn't there`); continue; }
    const js = readFileSync(path, "utf8");
    syntax(src, js);
    all.push(js);
  }
  if (all.length > 1) syntax(`${page}'s scripts together`, all.join("\n;\n"));
  for (const m of html.matchAll(/<link\s+href="([^"]+)"\s+rel="stylesheet">/g))
    if (!m[1].startsWith("/") && !existsSync(join(web, m[1]))) fail(page, `links ${m[1]}, which isn't there`);
}

// The 3D tab's own modules, each parsed as a module without being run.
const view3d = join(web, "view3d");
if (existsSync(view3d)) {
  for (const f of readdirSync(view3d).filter(f => f.endsWith(".mjs"))) {
    try { execFileSync(process.execPath, ["--check", join(view3d, f)], { stdio: "pipe" }); }
    catch (e) { fail(join(view3d, f), String(e.stderr || e.message).split("\n").slice(0, 4).join(" ")); }
  }
  // Every model its data module names is there.
  const names = [...readFileSync(join(view3d, "data.mjs"), "utf8").matchAll(/^export const MODELS = \{([\s\S]*?)\};/gm)]
    .flatMap(m => [...m[1].matchAll(/(\w+):\s*[\d.]+/g)].map(x => x[1]));
  if (names.length < 10) fail("view3d/data.mjs", "couldn't read the model list");
  for (const k of names) if (!existsSync(join(view3d, "models", k + ".glb"))) fail("view3d/models", `${k}.glb is missing`);
}

const json = (where) => {
  try { return JSON.parse(readFileSync(where, "utf8")); }
  catch (e) { fail(where, e.message); return null; }
};

// What's New: newest first, each entry a version, a date and some lines.
const news = json(join(web, "whats-new.json"));
if (news) {
  if (!Array.isArray(news.versions) || !news.versions.length) fail("whats-new.json", "no versions");
  for (const v of news.versions || []) {
    if (!/^\d+\.\d+\.\d+$/.test(v.version || "")) fail("whats-new.json", `bad version ${JSON.stringify(v.version)}`);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(v.date || "")) fail("whats-new.json", `${v.version}: bad date ${JSON.stringify(v.date)}`);
    if (!Array.isArray(v.items) || !v.items.length) fail("whats-new.json", `${v.version}: no items`);
  }
}

// Each theme folder: a theme.json BAMF can read, and its script and
// stylesheet, if it has them, with no syntax errors.
const themes = "themes";
const CATEGORIES = ["colours", "animated", "holiday"];
let count = 0;
for (const id of readdirSync(themes, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => d.name)) {
  const dir = join(themes, id);
  if (!/^[a-z0-9-]{1,32}$/.test(id)) fail(dir, "folder name must be a-z, 0-9 and -, up to 32 characters");
  const t = json(join(dir, "theme.json"));
  if (!t) continue;
  count++;
  if (typeof t.name !== "string" || !t.name.trim()) fail(dir, "theme.json has no name");
  if (!CATEGORIES.includes(t.category)) fail(dir, `category ${JSON.stringify(t.category)} isn't one of ${CATEGORIES.join(", ")}`);
  if (!Array.isArray(t.swatch) || !t.swatch.every(c => /^#[0-9a-fA-F]{6}$/.test(c))) fail(dir, "swatch should be a list of #rrggbb colours");
  if (!existsSync(join(dir, "theme.css")) && !existsSync(join(dir, "theme.js"))) fail(dir, "neither theme.css nor theme.js");
  if (existsSync(join(dir, "theme.js"))) {
    const js = readFileSync(join(dir, "theme.js"), "utf8");
    syntax(join(dir, "theme.js"), js);
    if (!js.includes("BAMF.registerTheme(")) fail(join(dir, "theme.js"), "never calls BAMF.registerTheme");
  }
}
if (!count) fail(themes, "no themes found");

if (problems.length) {
  console.error(problems.map(p => "  " + p).join("\n"));
  console.error(`\n${problems.length} problem${problems.length === 1 ? "" : "s"}.`);
  process.exit(1);
}
console.log(`OK: the dashboard's scripts, What's New and ${count} themes.`);
