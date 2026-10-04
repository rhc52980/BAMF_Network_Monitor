# BAMF for phones

A small Android and iPhone app that finds a BAMF server on the Wi-Fi network and
opens its dashboard. It does no monitoring of its own: a phone can't read the ARP
table or send raw packets, so BAMF keeps running on the machine that watches the
network, and the phone is a window onto it. Built with [Capacitor](https://capacitorjs.com).

## What it does

1. **Opens the server you used last**, if it still answers.
2. Otherwise **looks for BAMF on the phone's Wi-Fi network**: for each address in the
   phone's own subnet (never more than a /22) it asks for `/manifest.webmanifest`,
   which BAMF serves without sign-in and names "BAMF Network Monitor". An open port
   alone doesn't count; the name has to match.
3. **Takes an address you type**: `192.168.1.20`, `host:9000`, or a full `https://` URL
   for a server reached through a VPN or reverse proxy.
4. Hands the whole window to that server's dashboard. **Back** (Android) or a swipe
   from the left edge (iPhone) returns to the finder to pick another.

## Layout

| Path | What |
|---|---|
| `www/` | The finder page (`index.html`, `finder.js`), shared by both platforms |
| `android/app/src/main/java/.../BamfDiscoveryPlugin.java` | Discovery and opening, Android |
| `ios/App/App/BamfDiscoveryPlugin.swift` | The same, iPhone |
| `capacitor.config.json` | App id `com.leedellbayinnov.bamf` |

Both plugins offer `getNetworkInfo`, `discover` (with `serverFound` and `progress`
events), `cancel`, `check` and `openServer`.

## Build

```sh
cd mobile
npm install
npx cap sync

# Android: needs JDK 21 and the Android SDK (API 36)
cd android && ./gradlew assembleDebug        # app/build/outputs/apk/debug/app-debug.apk

# iPhone: needs Xcode
xcodebuild -project ios/App/App.xcodeproj -scheme App \
  -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
```

Run `npx cap sync` after changing anything in `www/`.

## Trying it without scanning a real network

The Android emulator's network is 10.0.2.0/24, and `10.0.2.2` is the host machine's
loopback, so BAMF running on the host is found there. Start BAMF from a published
build or with `ASPNETCORE_ENVIRONMENT=Development`, listening on `127.0.0.1:8840`.
To check that something else on 8840 is turned away, serve a manifest with another
name on that port and scan: it should report "No BAMF found".

## Limits, and what's next

- **Plain HTTP only is probed.** Discovery never trusts an unverified certificate. A
  server on its self-signed HTTPS port (8843) is entered by hand and needs a
  certificate the phone trusts; pinning a self-signed certificate on first connect is
  the planned next step.
- **HTTP is only opened for private addresses** (10/8, 172.16/12, 192.168/16,
  169.254/16, 100.64/10, loopback, `.local` and single-label names). HTTPS goes
  anywhere. This is checked in the plugin, not just in the page.
- **iPhone, what has and hasn't been checked.** Built for the simulator (iOS 17.2, iPhone
  SE), the finder renders at 375pt, the plugin registers, and a scan of the Mac's own /24
  runs and reports correctly. Not yet exercised: opening a server over HTTP (App Transport
  Security), swiping back to the finder, and a sign-in surviving a restart. Android has
  been through all three. Capacitor 8's `SceneDelegate.swift` creates the root view
  controller in code, so it has been changed to `BamfViewController()`; the storyboard is
  not used. If `npx cap sync` or an upgrade regenerates that file, change it back.
- **iPhone:** the first scan asks for Local Network permission; if that is refused,
  nothing is found and the address box is the way in. A `PrivacyInfo.xcprivacy` and
  store screenshots are still to do.
- **Android release build:** R8 shrinking and a release signing key are still to do.
