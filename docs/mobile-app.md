# BAMF on phones: how the mobile app was built and optimised

A hand-over note for the original developer. It says what was added, what was changed in
the original project (the web dashboard, the server's one route list, the Linux
installer), how the result was measured, and what is and is not verified.

Everything here was done on the `phone-app` branch. All numbers come from the scripts in
`mobile/tool/` and can be re-run; the method is in [How it was measured](#how-it-was-measured).

---

## 1. Summary

- **A phone app now exists** (`mobile/`, Android and iPhone). It finds a BAMF server on the
  phone's Wi-Fi network on its own, then shows that server's dashboard. A phone cannot scan a
  network itself (Android blocks the ARP table and raw sockets; iOS has no API for either), so
  the app is a window onto a BAMF server, not a monitor.
- **The dashboard was changed in a handful of places** so that it works well edge to edge
  inside a phone's WebView, and so that it stops burning the battery. None of those changes
  alters what it does with a mouse on a computer.
- **The biggest finding:** with the app open and nothing happening, the dashboard used
  **139% of a CPU core** (almost two). One CSS animation was the cause. Every animated
  theme used **87-278%**. After the changes the idle dashboard uses **5.7%** and no theme uses
  more than **9%**.

| Measured on a Pixel 10 Pro XL, Android 17, 120 Hz | Before | After |
|---|---|---|
| CPU, app on screen, idle for 60 s | **138.8%** of one core | **5.7%** |
| CPU, any of the 34 expensive themes | 87-278% (average 166%) | 5-9% (average 7.3%) |
| CPU, app in the background for 60 s | 1.45% | 1.08% |
| Scrolling: frames over 16 ms (legacy metric) | 17.3% | 2.5% |
| Scrolling: 90th / 99th percentile frame | 16 ms / 34 ms | 8 ms / 10 ms |
| Memory (app + WebView renderer, PSS) | 244 MB | 231 MB |
| Cold start, median of 5 (`am start -W`) | 218 ms | 205 ms |

---

## 2. What was built

### 2.1 The app (`mobile/`)

A [Capacitor](https://capacitorjs.com) 8 project, app id `com.leedellbayinnov.bamf`
(Android minSdk 24, target 36; iOS 15.0). The app bundles one tiny page of its own (the
*finder*, `mobile/www/`) and otherwise loads the dashboard from the server, so the dashboard
code is the same on every platform.

**The finder** (`www/index.html`, `www/finder.js`), shown at launch:

1. Reopens the server used last, if it still answers.
2. Otherwise **scans the phone's own subnet** for a BAMF server. A single result connects
   automatically; several are listed.
3. Takes an address typed by hand (`192.168.1.20`, `host:9000`, or a full `https://` URL for a
   server reached through a VPN or reverse proxy).
4. Hands the whole window to the server's dashboard. **Back** (Android) or an edge swipe
   (iPhone) returns to the finder, which does not reconnect by itself.

**Discovery** is a native plugin, written twice with the same contract:
`android/.../BamfDiscoveryPlugin.java` (Java) and `ios/App/App/BamfDiscoveryPlugin.swift`.

| Method | What it does |
|---|---|
| `getNetworkInfo()` | The phone's IPv4 address on Wi-Fi or Ethernet, and the range that will be scanned. Rejects with `no-wifi` on cellular or a VPN. |
| `discover({port})` | Probes every address of the phone's subnet (never wider than a /22, so at most 1,022 hosts; 64 at a time). Emits `serverFound` and `progress` events. |
| `cancel()` | Stops a scan. |
| `check({url})` | Is the server at this address BAMF? (a saved server, or one typed in) |
| `openServer({url})` | Validates the address, remembers its host, and loads it in the WebView. |

How a host is recognised: a TCP connect, then `GET /manifest.webmanifest`, which BAMF
serves **without sign-in**; the manifest's `name` must be `BAMF Network Monitor`. An open
port alone does not count, so a different service on 8840 is turned away (tested).

Design points worth knowing:

- **Only plain HTTP is probed.** Discovery contains no "trust every certificate" code
  (Google Play flags it). A server on its self-signed HTTPS port is typed in by hand and needs a
  certificate the phone trusts; pinning a self-signed certificate on first connect is the
  planned next step (see section 7).
- **Plain HTTP is opened only to private addresses** (10/8, 172.16/12, 192.168/16,
  169.254/16, 100.64/10, loopback, `.local` and single-label names); HTTPS goes anywhere.
  This is enforced in the plugin (`allowedTarget`), not just in the page.
- **Navigation stays inside the chosen server.** The plugin overrides `shouldOverrideLoad`
  (Capacitor's hook on both platforms) so that links within the server's host load in the app and
  everything else (for example a device's admin-page link) opens in the system browser.
- The scan is **cancelled when the app leaves the screen** (`handleOnPause`/`handleOnDestroy`
  on Android, `didEnterBackgroundNotification` on iOS). It is a burst of up to about 2,000
  connection attempts and has no business running unseen.

**Android specifics** (`MainActivity.java`, `AndroidManifest.xml`, `app/build.gradle`):

- Capacitor 8's Android core no longer handles the Back button, so `MainActivity` walks the WebView's
  history first and exits only when there is none. That is what returns to the finder.
- The WebView writes cookies to disk lazily, so a sign-in made just before the app is swiped
  away could be lost. `onPause` calls `CookieManager.flush()`. Verified: the session survives a
  force-stop.
- Permissions are `INTERNET` and `ACCESS_NETWORK_STATE` only. No wake locks.
- Release builds use **R8 and resource shrinking**; the upload key is read from a git-ignored
  `keystore.properties` (never committed), and builds are unsigned without it.

**iPhone specifics**:

- `SceneDelegate.swift` (generated by Capacitor 8) creates `CAPBridgeViewController()` in code
  and ignores the storyboard, so it was changed to `BamfViewController()`, which registers the
  plugin and turns on the left-edge swipe-back gesture. **Re-running `npx cap sync` or upgrading
  Capacitor can regenerate that file; change it back.**
- `Info.plist`: `NSLocalNetworkUsageDescription`, `NSAppTransportSecurity` ->
  `NSAllowsLocalNetworking` (this is what lets the WebView open an `http://` LAN address) and
  `ITSAppUsesNonExemptEncryption = false`.

**Icons and splash.** Capacitor's placeholder icons were replaced with the BAMF star, drawn from
`BAMF/wwwroot/bamf-icon.svg` by `mobile/tool/generate-icons.sh`: an adaptive icon, a
monochrome layer for Android 13+ themed icons, legacy square and round icons, the 1024 px iOS
icon, and dark splash screens (the Android 12+ splash background is set to the dashboard's
`#10141A`, so there is no white flash).

### 2.2 Tools (`mobile/tool/`)

| Tool | Purpose |
|---|---|
| `generate-icons.sh` | Regenerates every icon and splash image from the SVG. |
| `measure-android.sh` | The measurement behind section 4: cold start, memory, CPU on screen and in the background, scrolling jank. Run it before and after any change. |
| `ios-uitest/run.sh` | A UI test that drives the installed iPhone app on a simulator: sign in, relaunch and stay signed in, edge-swipe back to the finder. Needs Xcode and `xcodegen`; the password comes from `BAMF_PW` and is stored nowhere. |

---

## 3. Changes to the original project

Nothing here changes what the dashboard does with a mouse on a computer, except where marked
**(desktop-visible)**.

### 3.1 Dashboard (`BAMF/wwwroot`)

**Safe areas and pop-ups** (`css/dashboard.css`, `js/themes.js`). The phone app draws the page
edge to edge (the page already declared `viewport-fit=cover`), so the status bar, camera
cutout and gesture bar used to cover content.

- The header pads for `env(safe-area-inset-top/left/right)`, the page for the bottom inset.
- Elements **pinned to the screen edges** now add the insets to their own padding: the
  port-scan panel (`.scan-panel`), the plan drawing board (`.plan-ed`), the expanded map
  (`.map-card.expanded`), the blink bar and the toast.
- The theme menu lined its right edge up with its button's. On a phone the header wraps and the
  button is at the far left, so the menu ran off the left side. `themes.js` now caps its right
  offset so the left edge stays 8 px in. On a desktop the offset is unchanged.
- The insets are zero in an ordinary browser, so none of this moves anything there.

**Motion on touch screens** (the measured win; `css/dashboard.css`, `js/themes.js`,
`themes/*/theme.css`):

- Every reduced-motion rule that stops a *looping* animation now also applies when
  `(hover: none) and (pointer: coarse)` matches: the amber "unknown device" blink, the
  legend swatch, the empty-scan shimmer, the offline pin's ring, the screensaver camera dot, the
  map's cogs and packets, the theme layer (`#festive`), and **all 26 reduced-motion blocks in
  24 theme stylesheets**.
- `calmMotion()` in `js/themes.js` (what every scene script and the screensaver consult, via
  `ctx.calm`) uses the same media query list.
- **(desktop-visible, deliberately not changed)** A computer with a mouse matches neither query
  and behaves exactly as before. A *phone browser* now also gets the still look, not just the app.
- Left alone on purpose: the speed-test pulse (`.spd-running`) and the theme-switch splat, which
  run briefly and carry meaning.

**Nothing runs while hidden** (`js/main.js`, `js/core.js`). The 10-second data refresh and the
1-second countdown now skip while `document.hidden`; coming back to the page refreshes at once.
**(desktop-visible)** A background browser tab no longer polls. Checked: the dashboard does not
set the tab title, a favicon, a badge or a notification from that refresh, and alerts are sent
by the server (webhooks), so nothing anyone relies on changes. `wall.html` (the wall display)
has its own timers and was not touched or audited.

**Offline page** (`wwwroot/sw.js`, `wwwroot/offline.html`, `index.html`). A tiny service worker
answers *only* a page being opened while the server is unreachable, with a "Can't reach BAMF"
page. It never answers for the dashboard, `/api`, scripts, POSTs or the sign-in, so it cannot
show stale devices or fake a session. It registers only where the browser allows a service
worker (HTTPS or `localhost`); over plain HTTP on a LAN address nothing happens and BAMF behaves
as before. Bump `CACHE` in `sw.js` when `offline.html`, the icons or the fonts change.

### 3.2 Server (`BAMF/Api/ApiHelpers.cs`)

One change: `OpenPath` (the routes served without sign-in) gained `/sw.js` and `/offline.html`,
beside the manifest and icons that were already there. Neither holds anything private. The
manifest being open is also what lets the app recognise a server that has a password set.

### 3.3 Linux installer (`BAMF/linux/install.sh`)

Found while putting BAMF on a Debian 13 **arm64** VM, and fixed:

- It hard-coded `-r linux-x64`, so on an arm64 host it built a binary that cannot run. It now
  picks `linux-x64` or `linux-arm64` from `uname -m` and stops early on anything else.
- .NET aborts at startup without the ICU library, the SDK included, and a minimal Debian or
  Ubuntu lacks it. The installer now checks for `libicuuc` and installs the newest `libicuNN`
  the system offers, or says plainly that none is available.
- A successful install exited **1**: the cleanup trap's last command failed when there was no
  temporary folder. It now exits 0.

The READMEs list ICU as a Linux requirement and show the arm64 publish command. The release
workflow still builds only `linux-x64`; an arm64 release is separate work.

### 3.4 Tests, docs, ignore files

- `tests/web/sw.test.mjs` runs the real `sw.js` (offline page when the server is down; never
  answers `/api`, scripts or POSTs); `.github/scripts/check-web.mjs` syntax-checks it;
  `EndpointTests.cs` asserts `/sw.js` and `/offline.html` are reachable without sign-in.
- `BAMF/README.md` gained "When the server can't be reached", "Away from home" and "Mobile and
  battery"; `README.md` and `themes/README.md` document the touch-screen rule for theme authors
  (CSS: `@media (prefers-reduced-motion: reduce), (hover: none) and (pointer: coarse)`; script:
  `ctx.calm`). `.gitignore` / `.dockerignore` exclude `mobile/` build output and `node_modules`.

---

## 4. How it was measured

- **Device:** Pixel 10 Pro XL, Android 17 (API 37), 120 Hz display, Android System WebView
  153.0.8010.36, on Wi-Fi. A **real device**, as the optimisation rules require; emulator numbers
  are not representative.
- **Build:** the R8 release build, in both the before and the after runs.
- **Server:** BAMF on a Debian 13 arm64 VM bridged onto the LAN, with real devices to show
  (10-13 hosts), so the dashboard was rendering real data.
- **Script:** `mobile/tool/measure-android.sh`, unchanged between runs. CPU is utime+stime from
  `/proc/<pid>/stat` for the app process and the WebView renderer process(es), read before and
  after a 60 s window, reported as a percentage of **one** core (139% = 1.39 cores). While the
  app is "idle" the script sends a wake-up key every 10 s to stop the screen timing out; the
  same in both runs.
- **Cold start** is the untraced `am start -W` `TotalTime`, median of 5 (tracing inflates it).
- **Per-theme sweep:** the debug build with Chrome DevTools attached to the WebView; each theme
  applied with `applyTheme(id)`, 4 s to settle, then 12 s measured. 46 themes, before and after.
- **Not measured:** iPhone CPU and memory (the simulator's numbers are not meaningful; use
  Instruments on a device), other Android devices, battery drain over time.

### Root cause

Disabling every CSS animation in the live page dropped the app process from 117% to 4% and the
renderer from 71% to 2.6%. With the default Dark theme the page had only 11 running animations,
all one `blink` (`opacity`, `steps(2)`, 1.1 s) on the amber "unknown device" tiles and the
legend, and **no** `requestAnimationFrame` callbacks. Chromium keeps producing frames at the
display's refresh rate for as long as any animation loops, even one that visibly changes twice a
second, and Android's WebView redraws the whole screen each frame. Ten tiny blinking squares
therefore cost almost two cores.

Two readings that turned out wrong and are worth knowing: the 1,218 frames counted during the
scroll test looked like 100 fps of idle redrawing but were just the scrolling itself, and the
shared scene helper (`seasonCanvas`) already capped canvases at ~30 fps and paused when hidden;
the cost was in the looping animations, not in those loops.

### Memory

The app process is dominated by **graphics buffers: 101.7 MB** (the full-screen WebView
surface on a 1080x2404 display), against 4.1 MB of Java heap, 12.6 MB of native heap and 12 MB of
code. That is inherent to a full-screen WebView and not reducible without lowering the
resolution. The renderer fell from 89 MB to 72 MB once nothing was animating; the app process
is unchanged (155 -> 159 MB, within noise).

### Per-theme CPU (percent of one core, app + renderer)

Plain themes (12) were 5-12% before and 5-9% after. The 34 that animate:

| Theme | Category | Before (app + renderer = total) | After (total) |
|---|---|---|---|
| datacentre | animated | 186% + 92% = **278%** | 7% |
| aquarium | animated | 186% + 85% = **271%** | 6% |
| nightstreet | animated | 183% + 87% = **271%** | 6% |
| waterworks | animated | 198% + 48% = **245%** | 9% |
| powerplant | animated | 171% + 73% = **244%** | 7% |
| goatnight |  | 140% + 101% = **241%** | 6% |
| rgb | animated | 165% + 76% = **241%** | 7% |
| christmas | holiday | 166% + 73% = **240%** | 8% |
| factorynight |  | 136% + 79% = **216%** | 6% |
| factory | animated | 134% + 80% = **215%** | 8% |
| steampunk | animated | 147% + 69% = **215%** | 8% |
| synthwave | colours | 164% + 50% = **213%** | 7% |
| goat | animated | 130% + 83% = **212%** | 7% |
| clawdusk |  | 140% + 71% = **211%** | 8% |
| claw | animated | 137% + 69% = **206%** | 8% |
| halloween | holiday | 105% + 73% = **178%** | 7% |
| storm | animated | 118% + 50% = **168%** | 8% |
| laser | animated | 104% + 53% = **156%** | 7% |
| hotdog | animated | 101% + 27% = **128%** | 7% |
| terminal | animated | 75% + 53% = **127%** | 8% |
| antfarm | animated | 84% + 37% = **121%** | 9% |
| cottoncandy | colours | 94% + 21% = **114%** | 8% |
| spring | animated | 73% + 32% = **105%** | 7% |
| thanksgiving | holiday | 61% + 42% = **104%** | 8% |
| woodlands | animated | 67% + 36% = **102%** | 6% |
| harbour | animated | 66% + 35% = **101%** | 5% |
| railway | animated | 65% + 32% = **97%** | 9% |
| summer | animated | 67% + 30% = **97%** | 6% |
| departures | animated | 58% + 34% = **92%** | 8% |
| constellation | animated | 56% + 34% = **90%** | 7% |
| newyear | holiday | 62% + 29% = **90%** | 9% |
| matrix | animated | 57% + 32% = **88%** | 6% |
| winter | animated | 58% + 30% = **87%** | 8% |
| amber | colours | 56% + 12% = **68%** | 8% |
| hackerred | colours | 7% + 5% = **12%** | 8% |
| blueprint | colours | 5% + 4% = **9%** | 7% |
| nord | colours | 5% + 4% = **9%** | 7% |
| solar-light | colours | 4% + 5% = **9%** | 7% |
| c64 | colours | 5% + 3% = **8%** | 7% |
| dracula | colours | 4% + 4% = **8%** | 8% |
| dark | built in | 4% + 3% = **7%** | 9% |
| contrast | built in | 4% + 3% = **7%** | 8% |
| solarized | colours | 4% + 3% = **7%** | 5% |
| gameboy | colours | 3% + 2% = **6%** | 7% |
| gruvbox | colours | 4% + 3% = **6%** | 7% |
| light | built in | 2% + 2% = **5%** | 8% |

Totals: the 34 expensive themes averaged **166%** (maximum 278%) before and **7.3%** (maximum
9%) after; the 12 plain themes averaged 7.8% before and 7.3% after.

---

## 5. Checklist against the optimisation rules

| Rule (`Claude/rules/`) | Status |
|---|---|
| Cap the frame rate; no continuous animation burning the display | Done for touch screens: looping animations and scenes are held still. (The shared scene helper already capped canvases at ~30 fps.) |
| Do not hold wake locks | None used; no wake-lock permission. |
| No background work | Refresh and countdown pause while hidden; discovery scans are cancelled when the app leaves the screen. Background CPU 1.08%. |
| Defer non-critical startup work past the first frame | There is no ad or analytics SDK. The finder does its work after the page loads. Cold start 205 ms. |
| Target API 36, AAB for upload | Target 36, `bundleRelease` produces the AAB. |
| 16 KB page size | The APK has no native libraries; `zipalign -c -P 16 4` passes. |
| R8 / shrink | On (release APK about 1.1 MB, debug about 4 MB); the plugin survives it (checked by running the minified build). |
| Android vitals (crash/ANR/wake locks) | No crash or reflection error in the minified build; scrolling jank 2.5%; no wake locks. Not measured on Play (nothing is published). |
| Measure on a real device before and after | Done, section 4. |
| iOS: `PrivacyInfo.xcprivacy` | **Not done yet.** |
| iOS: ATT | Not applicable (no ads, no tracking). |
| iOS: `ITSAppUsesNonExemptEncryption` | Set to `false` (HTTPS only). |
| iOS: measure on a device | **Not done**; simulator only. |

---

## 6. Verification

- C#: 238 tests pass. Web: `check-web.mjs` and 12 node tests pass.
- Android: fresh-install discovery and connect on an emulator; discovery, connect, Back to the
  finder, a session that survives a force-stop and a decoy service on 8840 being rejected, all
  on the emulator and on the Pixel.
- iPhone (simulator, iOS 17.2, iPhone SE): discovery of a BAMF server on the Mac's /24, opening
  it over plain HTTP, sign-in, a session that survives a full relaunch, and an edge swipe back to
  the finder that does not reconnect. The UI test passed on 5 of 6 runs; the sixth timed out at 97 s
  and could not be reproduced (raw scan time is a steady 6-7 s; direct launches reached the
  sign-in page in about 13-20 s). Its cause is unidentified and looks like harness slowness; it
  is mentioned so nobody assumes the test is infallible.

---

## 7. Not done, not verified, recommendations

1. **Animated themes are still on a phone, by design.** A user who chose Aquarium on a phone now
   sees it still. If that is not wanted, the right fix is a per-device "Animate themes" setting.
   It needs a class on `<html>` and class-based selectors in place of the media query, because
   CSS cannot be toggled from script. It is not built.
2. **Version bump and What's New.** The dashboard's behaviour changed on touch screens and in
   hidden tabs, but `BAMF.csproj` `<Version>` (2.0.0) and `wwwroot/whats-new.json` were not
   touched; that is a release decision.
3. **iPhone:** a real-device run (and Instruments for CPU/memory), the Local Network permission
   prompt (the simulator does not show one), `PrivacyInfo.xcprivacy`, and store screenshots.
4. **Android release:** the upload key (create it, put it in `keystore.properties`), Play
   Console reviewer access (a demo server or demo mode; the app does nothing without a BAMF
   server), and whether current Android versions need a local-network runtime permission for the
   probe (the Pixel on Android 17 worked without one).
5. **Self-signed HTTPS:** pin the certificate on first connect (with a "certificate changed,
   trust the new one?" prompt), then drop the Android cleartext allowance for HTTPS servers.
6. **Android backups:** the app allows Android's backup (the Capacitor default), so saved servers
   and the sign-in cookie can reach Google backups; `android:allowBackup="false"` is advisable.
7. **Release workflow:** only `linux-x64` is built; an arm64 build would make the installer fix
   usable from a release.
8. **Server version coupling:** the safe-area and motion fixes live in the dashboard, so a
   server still on an older BAMF will show the status-bar overlap and the animation cost inside the
   app.
9. **Test deployments:** while testing, dashboard files were copied by hand onto a test VM. The
   repository is the source of truth; the installer's update path rebuilds from source.

---

## 7a. Reproducing the numbers

```sh
# Android, on a connected phone that has already connected to a server once
bash mobile/tool/measure-android.sh [adb-serial]
FG_SECONDS=60 BG_SECONDS=60 RUNS=5 bash mobile/tool/measure-android.sh

# iPhone UI test (simulator)
BAMF_PW=<server password> bash mobile/tool/ios-uitest/run.sh

# Release build
cd mobile && npx cap sync android && cd android && ./gradlew bundleRelease assembleRelease

# Icons, after changing BAMF/wwwroot/bamf-icon.svg
bash mobile/tool/generate-icons.sh
```

The per-theme sweep used Chrome DevTools against the debug build's WebView
(`adb forward tcp:9333 localabstract:webview_devtools_remote_<pid>`, then `applyTheme(id)` and
CPU ticks from `/proc`); it is not packaged as a tool.

---

## 8. Commits on `phone-app`

| Commit | What |
|---|---|
| `616c7db` | Offline page and service worker; phone-use docs |
| `324f31d` | The phone app (`mobile/`) |
| `fcb5310` | Android release with R8 and resource shrinking |
| `d8a061d` | Dashboard header and last row clear of the system bars |
| `c185792` | Linux installer: architecture and ICU |
| `b30f56b` | Installer exits 0 on success |
| `06b0502` | BAMF star icons and splash screens |
| `696f19d` | README: what the iPhone simulator test showed |
| `79fedd9` | Edge-pinned panels and the theme menu on screen |
| `fd25752` | iPhone UI test |
| `5ba490a` | Looping animations still on touch screens; no polling while hidden |
| `d42b6f0` | Scan cancelled when the app leaves the screen; `measure-android.sh` |

The optimisation round in section 1 is `5ba490a` (the web changes and README sections) and
`d42b6f0` (the native scan cancel and the measuring tool). This document was added after them.
