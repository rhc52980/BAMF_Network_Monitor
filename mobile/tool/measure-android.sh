#!/usr/bin/env bash
# Measures the Android app on a connected phone, the way the optimisation rules
# ask: on real hardware, with the plain `am start -W` cold-start number (no
# tracing), and before and after any change. Reports cold start, memory (the app
# and its WebView renderer), CPU while the app is on screen and while it is in the
# background, and frame jank while scrolling the dashboard.
#
#   bash mobile/tool/measure-android.sh [adb-serial]
#   FG_SECONDS=60 BG_SECONDS=60 RUNS=5 bash mobile/tool/measure-android.sh
#
# The app has to be installed and have connected to a server once (so it can open
# the dashboard on launch). The phone must be awake and unlocked; the script taps
# nothing except Home and some scrolling swipes.
set -euo pipefail

PKG=com.leedellbayinnov.bamf
ADB=(adb); [ -n "${1:-}" ] && ADB=(adb -s "$1")
FG="${FG_SECONDS:-60}"; BG="${BG_SECONDS:-60}"; RUNS="${RUNS:-5}"
sh() { "${ADB[@]}" shell "$@" | tr -d '\r'; }

# utime + stime, in clock ticks, summed over the given pids.
ticks() {
  local total=0 v p
  for p in "$@"; do
    v="$(sh "cat /proc/$p/stat 2>/dev/null" | sed 's/^.*) //' | awk '{print $12 + $13}')"
    total=$(( total + ${v:-0} ))
  done
  echo "$total"
}
sandboxed() { sh 'ps -A -o PID,NAME | grep sandboxed_process' | awk '{print $1}' | sort -n; }
pss() { sh "dumpsys meminfo $1" | awk '/TOTAL PSS:/ {print $3; exit} /^ *TOTAL +[0-9]/ {print $2; exit}'; }
# Waits N seconds with the screen kept on (a wake-up key every 10s resets its timeout).
idle_awake() { local n="$1"; while [ "$n" -gt 0 ]; do sh "input keyevent KEYCODE_WAKEUP" >/dev/null; sleep 10; n=$(( n - 10 )); done; }
cpu_pct() { awk -v a="$1" -v b="$2" -v s="$3" -v hz="$HZ" 'BEGIN { printf "%.2f", (b - a) / hz / s * 100 }'; }

echo "Device: $(sh getprop ro.product.model), Android $(sh getprop ro.build.version.release) (API $(sh getprop ro.build.version.sdk))"
echo "WebView: $(sh dumpsys webviewupdate | awk -F': ' '/Current WebView package/ {print $2; exit}')"
HZ="$(sh getconf CLK_TCK)"; HZ="${HZ:-100}"

# --- cold start ----------------------------------------------------------------
starts=()
for i in $(seq 1 "$RUNS"); do
  sh "am force-stop $PKG"; sleep 3
  t="$(sh "am start -W -n $PKG/.MainActivity" | awk '/TotalTime/ {print $2}')"; starts+=("$t")
done
sorted="$(printf '%s\n' "${starts[@]}" | sort -n | tr '\n' ' ')"
median="$(printf '%s\n' "${starts[@]}" | sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}')"
echo "Cold start (am start -W TotalTime, ms): $sorted-> median $median"

# --- steady state, app on screen -----------------------------------------------
sh "am force-stop $PKG"; sleep 3
before="$(sandboxed)"
sh "am start -n $PKG/.MainActivity" >/dev/null
sleep 30                                           # connect, sign in from the saved session, load the dashboard
APP="$(sh "pidof $PKG" | awk '{print $1}')"
[ -n "$APP" ] || { echo "The app isn't running; open it and connect to a server first."; exit 1; }
REND="$(comm -13 <(echo "$before") <(sandboxed) | tr '\n' ' ')"
echo "Processes: app $APP, WebView renderers: ${REND:-none found}"
echo "Memory on the dashboard (PSS, MB): app $(awk -v k="$(pss "$PKG")" 'BEGIN{printf "%.0f", k/1024}')" \
     "+ renderers $(for r in $REND; do pss "$r"; done | awk '{s+=$1} END {printf "%.0f", s/1024}')"

a="$(ticks $APP $REND)"; idle_awake "$FG"; b="$(ticks $APP $REND)"
echo "CPU on screen, idle for ${FG}s: $(cpu_pct "$a" "$b" "$FG")% of one core"

# --- scrolling jank --------------------------------------------------------------
sh "dumpsys gfxinfo $PKG reset" >/dev/null
for i in 1 2 3 4; do
  sh "input swipe 540 1700 540 500 350"; sleep 0.6
  sh "input swipe 540 500 540 1700 350"; sleep 0.6
done
sleep 1
sh "dumpsys gfxinfo $PKG" | grep -E "Total frames rendered|Janky frames|50th percentile|90th percentile|95th percentile|99th percentile" | sed 's/^ */  /'

# --- background -----------------------------------------------------------------
sh "input keyevent KEYCODE_HOME"; sleep 5
a="$(ticks $APP $REND)"; sleep "$BG"; b="$(ticks $APP $REND)"
echo "CPU in the background for ${BG}s: $(cpu_pct "$a" "$b" "$BG")% of one core"
sh "am start -n $PKG/.MainActivity" >/dev/null
