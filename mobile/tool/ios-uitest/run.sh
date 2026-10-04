#!/usr/bin/env bash
# Runs the iPhone app through sign-in, a restart and the swipe back, on a
# simulator, with a UI test that drives the installed app by its bundle id.
#
#   BAMF_PW=<the BAMF server's password> bash mobile/tool/ios-uitest/run.sh ["Simulator name"]
#
# It builds the app, installs it fresh (so discovery runs from scratch), runs
# the test and shuts the simulator down again if it booted it. The app scans
# the Mac's Wi-Fi subnet for a BAMF server, so one has to be running there.
# Needs Xcode and xcodegen (brew install xcodegen). Screenshots of each step
# are left in $TMPDIR/bamf-ios-uitest/shots.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
SIM_NAME="${1:-iPhone SE (3rd generation)}"
BUNDLE="com.leedellbayinnov.bamf"
WORK="${TMPDIR:-/tmp}/bamf-ios-uitest"

[ -n "${BAMF_PW:-}" ] || { echo "Set BAMF_PW to the password of the BAMF server the app will find."; exit 1; }
command -v xcodegen >/dev/null || { echo "Needs xcodegen: brew install xcodegen"; exit 1; }

UDID="$(xcrun simctl list devices available | grep -F "$SIM_NAME (" | head -1 | sed -E 's/.*\(([0-9A-F-]{36})\).*/\1/')"
[ -n "$UDID" ] || { echo "No available simulator named \"$SIM_NAME\"."; exit 1; }
mkdir -p "$WORK/shots"; rm -f "$WORK"/shots/*.png

BOOTED_HERE=0
if ! xcrun simctl list devices booted | grep -q "$UDID"; then xcrun simctl boot "$UDID"; BOOTED_HERE=1; fi
trap 'if [ "$BOOTED_HERE" = 1 ]; then xcrun simctl shutdown "$UDID" >/dev/null 2>&1 || true; fi' EXIT
xcrun simctl bootstatus "$UDID" >/dev/null

echo "==> Building the app for $SIM_NAME"
if ! xcodebuild -project "$ROOT/mobile/ios/App/App.xcodeproj" -scheme App \
     -destination "platform=iOS Simulator,id=$UDID" -derivedDataPath "$WORK/app" \
     CODE_SIGNING_ALLOWED=NO build > "$WORK/app-build.log" 2>&1; then
  tail -20 "$WORK/app-build.log"; exit 1
fi
xcrun simctl uninstall "$UDID" "$BUNDLE" 2>/dev/null || true
xcrun simctl install "$UDID" "$WORK/app/Build/Products/Debug-iphonesimulator/App.app"

echo "==> Running the UI test"
(cd "$HERE" && xcodegen generate --quiet)
set +e
TEST_RUNNER_BAMF_PW="$BAMF_PW" TEST_RUNNER_BAMF_SHOTS="$WORK/shots" \
  xcodebuild test -project "$HERE/BamfUITests.xcodeproj" -scheme BamfUITests \
  -destination "platform=iOS Simulator,id=$UDID" -derivedDataPath "$WORK/uitest" \
  CODE_SIGNING_ALLOWED=NO > "$WORK/uitest.log" 2>&1
RESULT=$?
set -e
grep -E "Test Case .* (passed|failed)|error:|TEST (SUCCEEDED|FAILED)" "$WORK/uitest.log" | cut -c1-200 || true
echo "Screenshots: $WORK/shots   Full log: $WORK/uitest.log"
exit $RESULT
